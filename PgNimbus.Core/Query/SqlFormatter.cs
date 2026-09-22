using System.Text;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Query;

/// <summary>
/// A pragmatic, dependency-free SQL pretty-printer following the layout of
/// <see href="https://www.sqlstyle.guide/">sqlstyle.guide</see>: root keywords are
/// right-aligned so they all end at the same column, forming a whitespace "river"
/// between keyword and content (<c>SELECT</c> at the margin, <c>&#160;&#160;FROM</c>,
/// <c>&#160;WHERE</c>, right-aligned <c>AND</c>/<c>OR</c>), <c>ON</c> indented under
/// its <c>JOIN</c>, and subqueries forming their own nested river anchored at their
/// opening paren. Reserved keywords are upper-cased. Layout is width-aware: a clause
/// whose items all fit within <see cref="MaxCompactWidth"/> columns stays on the
/// keyword's line (<c>SELECT id, name</c>); longer lists break one item per line
/// aligned on the content side of the river.
/// </summary>
/// <remarks>
/// <para>
/// This is a lexer-driven formatter, not a parser: it understands enough Postgres
/// syntax to tokenize faithfully (respecting string/dollar/quoted-identifier and
/// comment contexts, exactly like <see cref="SqlScriptSplitter"/>) and then applies
/// layout heuristics to the token stream. It deliberately gives up gracefully on
/// shapes it can't lay out well rather than guessing.
/// </para>
/// <para>
/// <b>Safety net:</b> whatever layout it produces, <see cref="Format"/> re-tokenizes
/// its own output and compares it token-for-token against the input (case-insensitive
/// for keywords/identifiers, exact for literals and comments). If they don't match —
/// i.e. formatting would have dropped, reordered, or altered a token — it returns the
/// original text untouched. So a bad layout can look ugly, but it can never corrupt a
/// query.
/// </para>
/// </remarks>
public static class SqlFormatter
{
    // The river column: root keywords right-align to end here ("SELECT".Length,
    // per sqlstyle.guide). Content starts one space to the right.
    private const int RiverWidth = 6;

    // A clause whose whole body fits on the keyword's line within this many
    // columns stays on one line instead of breaking one item per line.
    private const int MaxCompactWidth = 80;

    /// <summary>
    /// Formats a single SQL statement. Returns the input unchanged when it is empty,
    /// comment-only, or when the formatted output wouldn't round-trip to the same
    /// tokens (the safety net described in the type remarks).
    /// </summary>
    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var tokens = Tokenize(sql);
        if (tokens.Count == 0)
        {
            return sql;
        }

        var lines = Layout(tokens);
        Compact(lines);
        var formatted = Render(lines);

        // Never corrupt: if our output doesn't re-tokenize to the same token
        // sequence, hand the original back verbatim.
        return SameTokens(tokens, Tokenize(formatted)) ? formatted : sql;
    }

    private enum Kind
    {
        Word, Number, Str, QuotedId, Dollar, LineComment, BlockComment,
        Comma, OpenParen, CloseParen, Semicolon, Dot, Op,
    }

    private readonly record struct Tok(Kind Kind, string Text, bool SpaceBefore);

    // ---- Layout -----------------------------------------------------------

    // What started an output line — Compact uses this to know which lines are
    // clause keywords, which are wrapped list items that may fold back onto
    // them, and which must always keep their own line (JOINs, AND/OR).
    private enum LineKind { ClauseHead, Item, AndOr, Join, Other }

    private sealed class Line(int column, LineKind kind)
    {
        public int Column { get; } = column;
        public LineKind Kind { get; } = kind;
        public StringBuilder Text { get; } = new();
    }

    private static List<Line> Layout(List<Tok> tokens)
    {
        var lines = new List<Line>();
        Line? cur = null;

        var riverEnd = RiverWidth;  // column clause keywords right-align to (grows inside subqueries)
        var bodyActive = false;     // are we in a clause whose items/AND/OR break one-per-line?
        var clauseParenDepth = 0;   // paren depth the current clause lives at
        var parenDepth = 0;
        var prevJoinMod = false;    // last emitted token was a JOIN modifier (LEFT/INNER/…)
        var betweenDepth = 0;       // BETWEENs awaiting their AND, so "a BETWEEN x AND y" never breaks at that AND
        var joinContext = false;    // between a JOIN and its ON/USING, which get their own line
        var glueClause = false;     // next clause keyword opens a subquery — keep it glued to its "("
        string? prevKeyword = null; // last keyword emitted, lower-cased (for DELETE FROM etc.)
        var prevKind = Kind.Semicolon; // last emitted token, for suppressing the following space
        var prevText = "";

        // When set, the next emitted token starts a fresh line there.
        (int Column, LineKind Kind)? pending = (0, LineKind.ClauseHead);

        // Per-open-paren saved clause state, restored on the matching close.
        var stack = new Stack<(int RiverEnd, bool BodyActive, int ClauseParenDepth, bool PrevJoinMod, string? PrevKeyword, int BetweenDepth, bool JoinContext)>();

        void Break(int column, LineKind kind) => pending = (Math.Max(0, column), kind);

        void Emit(string text, Kind kind, bool spaceBefore)
        {
            if (cur is null || pending is not null)
            {
                // The statement's very first line sits at the margin whatever
                // its keyword's river position would be.
                var (column, lineKind) = pending ?? (0, LineKind.Other);
                cur = new Line(lines.Count == 0 ? 0 : column, lineKind);
                lines.Add(cur);
            }

            pending = null;

            // A "." , "(" , or "::" glues to whatever follows it, whatever the token asks.
            var space = spaceBefore
                && prevKind != Kind.Dot
                && prevKind != Kind.OpenParen
                && !(prevKind == Kind.Op && prevText == "::");
            if (cur.Text.Length > 0 && space)
            {
                cur.Text.Append(' ');
            }

            cur.Text.Append(text);
            prevKind = kind;
            prevText = text;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var atClause = parenDepth == clauseParenDepth;
            var lower = t.Kind == Kind.Word ? t.Text.ToLowerInvariant() : null;

            switch (t.Kind)
            {
                case Kind.Word when atClause && TryTwoWordClause(tokens, i, out var clauseText, out var advance):
                {
                    // Only the first word right-aligns to the river; "BY" spills right.
                    var firstLength = clauseText.IndexOf(' ');
                    Break(riverEnd - firstLength, LineKind.ClauseHead);
                    Emit(clauseText, Kind.Word, spaceBefore: true);
                    bodyActive = true;
                    clauseParenDepth = parenDepth;
                    prevKeyword = clauseText.ToLowerInvariant();
                    prevJoinMod = false;
                    joinContext = false;
                    betweenDepth = 0;
                    i += advance;
                    break;
                }

                case Kind.Word when atClause && MajorBreak.Contains(lower!):
                    // "DELETE FROM" / "INSERT INTO … SELECT": keep FROM glued to the
                    // verb it follows rather than starting a new line.
                    if (lower == "from" && prevKeyword == "delete")
                    {
                        Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                        bodyActive = false;
                    }
                    else
                    {
                        // A subquery's first keyword stays glued to its "(";
                        // anywhere else the keyword right-aligns to the river,
                        // with its body flowing on the same line.
                        if (glueClause)
                        {
                            glueClause = false;
                        }
                        else
                        {
                            Break(riverEnd - lower!.Length, LineKind.ClauseHead);
                        }

                        Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                        clauseParenDepth = parenDepth;
                        bodyActive = BodyForcing.Contains(lower!);
                    }

                    prevKeyword = lower;
                    prevJoinMod = false;
                    joinContext = false;
                    betweenDepth = 0;
                    break;

                case Kind.Word when atClause && JoinModifiers.Contains(lower!):
                    if (!prevJoinMod)
                    {
                        Break(riverEnd - lower!.Length, LineKind.Join);
                    }

                    Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                    prevJoinMod = true;
                    prevKeyword = lower;
                    break;

                case Kind.Word when atClause && lower == "join":
                    if (!prevJoinMod)
                    {
                        Break(riverEnd - 4, LineKind.Join);
                    }

                    Emit("JOIN", Kind.Word, spaceBefore: true);
                    prevJoinMod = false;
                    prevKeyword = lower;
                    joinContext = true;
                    break;

                case Kind.Word when atClause && joinContext && lower is "on" or "using":
                    // The join condition gets its own line on the content side
                    // of the river, under the table it joins.
                    Break(riverEnd + 1, LineKind.Other);
                    Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                    prevKeyword = lower;
                    prevJoinMod = false;
                    joinContext = false;
                    break;

                case Kind.Word when atClause && bodyActive && (lower == "and" || lower == "or"):
                    // The AND that closes "a BETWEEN x AND y" is part of the
                    // expression, not a predicate separator — keep it inline.
                    if (lower == "and" && betweenDepth > 0)
                    {
                        betweenDepth--;
                    }
                    else
                    {
                        Break(riverEnd - lower!.Length, LineKind.AndOr);
                    }

                    Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                    prevJoinMod = false;
                    prevKeyword = lower;
                    break;

                case Kind.Word:
                    if (lower == "between")
                    {
                        betweenDepth++;
                    }
                    else if (lower == "and" && betweenDepth > 0)
                    {
                        betweenDepth--;
                    }

                    Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                    prevJoinMod = false;
                    if (Keywords.Contains(lower!))
                    {
                        prevKeyword = lower;
                    }

                    break;

                case Kind.Comma:
                    // No space before the comma; break the list only at clause level
                    // (inside a function-call paren, keep arguments on one line).
                    Emit(",", Kind.Comma, spaceBefore: false);
                    if (atClause && bodyActive)
                    {
                        Break(riverEnd + 1, LineKind.Item);
                    }

                    prevJoinMod = false;
                    break;

                case Kind.OpenParen:
                {
                    var subquery = IsSubqueryOpen(tokens, i);
                    // A "(" hugs the token before it only when the source had no space
                    // there and that token is callable/subscriptable — so "count(*)"
                    // stays tight while "users (…)" and "in (…)" keep their space.
                    var funcCall = !t.SpaceBefore && i > 0 && IsCallTarget(tokens[i - 1]);
                    stack.Push((riverEnd, bodyActive, clauseParenDepth, prevJoinMod, prevKeyword, betweenDepth, joinContext));
                    betweenDepth = 0;
                    joinContext = false;

                    Emit("(", Kind.OpenParen, spaceBefore: !funcCall);
                    parenDepth++;

                    if (subquery)
                    {
                        // The subquery's river anchors at its "(": the inner
                        // SELECT starts right after it and inner clause
                        // keywords right-align to where that SELECT ends.
                        var parenColumn = cur!.Column + cur.Text.Length - 1;
                        riverEnd = parenColumn + 1 + RiverWidth;
                        bodyActive = false;
                        clauseParenDepth = parenDepth;
                        glueClause = true;
                    }

                    prevJoinMod = false;
                    break;
                }

                case Kind.CloseParen:
                    parenDepth = Math.Max(0, parenDepth - 1);
                    if (stack.Count > 0)
                    {
                        var s = stack.Pop();
                        riverEnd = s.RiverEnd;
                        bodyActive = s.BodyActive;
                        clauseParenDepth = s.ClauseParenDepth;
                        prevJoinMod = s.PrevJoinMod;
                        prevKeyword = s.PrevKeyword;
                        betweenDepth = s.BetweenDepth;
                        joinContext = s.JoinContext;
                    }

                    // ")" hugs the last token of what it closes, even for a
                    // multi-line subquery (per sqlstyle.guide).
                    Emit(")", Kind.CloseParen, spaceBefore: false);
                    break;

                case Kind.Semicolon:
                    Emit(";", Kind.Semicolon, spaceBefore: false);
                    // Reset clause state for a following statement.
                    riverEnd = RiverWidth;
                    bodyActive = false;
                    clauseParenDepth = parenDepth;
                    prevKeyword = null;
                    prevJoinMod = false;
                    joinContext = false;
                    betweenDepth = 0;
                    if (i < tokens.Count - 1)
                    {
                        Break(0, LineKind.ClauseHead);
                    }

                    break;

                case Kind.Dot:
                    Emit(".", Kind.Dot, spaceBefore: false);
                    prevJoinMod = false;
                    break;

                case Kind.LineComment:
                    Emit(t.Text, Kind.LineComment, spaceBefore: true);
                    Break(riverEnd + 1, LineKind.Item); // a line comment runs to EOL
                    prevJoinMod = false;
                    break;

                case Kind.Op:
                    // "::" cast and a "*" right after a dot ("t.*") hug their neighbours.
                    var hug = t.Text == "::" || (t.Text == "*" && i > 0 && tokens[i - 1].Kind == Kind.Dot);
                    Emit(t.Text, Kind.Op, spaceBefore: !hug);
                    prevJoinMod = false;
                    break;

                default: // Number, Str, QuotedId, Dollar, BlockComment
                    Emit(t.Text, t.Kind, spaceBefore: true);
                    prevJoinMod = false;
                    break;
            }
        }

        return lines;
    }

    private static string Render(List<Line> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(' ', line.Column).Append(line.Text);
        }

        return sb.ToString();
    }

    // ---- Compaction -------------------------------------------------------

    // Folds a clause's wrapped items back onto the keyword's line when the
    // whole clause fits: "SELECT id,\n       name" → "SELECT id, name". Only
    // Item lines fold — JOINs and AND/OR always keep their own line, per the
    // style guide. Every fold is validated by re-tokenizing: a fold that would
    // swallow code into a line comment or reflow a multi-line literal is
    // rejected. Finishes by pairing LIMIT/OFFSET/FETCH onto one line.
    private static void Compact(List<Line> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != LineKind.ClauseHead)
            {
                continue;
            }

            var end = i + 1;
            while (end < lines.Count && lines[end].Kind == LineKind.Item)
            {
                end++;
            }

            if (end > i + 1)
            {
                TryFold(lines, i, end);
            }
        }

        MergePagination(lines);
    }

    private static void TryFold(List<Line> lines, int start, int end)
    {
        var texts = new string[end - start];
        for (var i = start; i < end; i++)
        {
            texts[i - start] = lines[i].Text.ToString();
            if (texts[i - start].Contains('\n'))
            {
                return; // a multi-line literal or comment never folds
            }
        }

        var folded = string.Join(' ', texts);
        if (lines[start].Column + folded.Length > MaxCompactWidth
            || !SameTokens(Tokenize(string.Join('\n', texts)), Tokenize(folded)))
        {
            return;
        }

        lines[start].Text.Clear();
        lines[start].Text.Append(folded);
        lines.RemoveRange(start + 1, end - start - 1);
    }

    // "LIMIT 100" + "OFFSET 0" (and an ANSI "FETCH …" after either) read as one
    // pagination clause, so they share a line when the pair fits.
    private static void MergePagination(List<Line> lines)
    {
        for (var i = lines.Count - 1; i > 0; i--)
        {
            if (lines[i].Kind != LineKind.ClauseHead || lines[i - 1].Kind != LineKind.ClauseHead)
            {
                continue;
            }

            var cur = lines[i].Text.ToString();
            var prev = lines[i - 1].Text.ToString();
            var curFirst = FirstWordLower(cur);
            var prevFirst = FirstWordLower(prev);
            var pair = (curFirst == "offset" && prevFirst == "limit")
                || (curFirst == "fetch" && prevFirst is "offset" or "limit");
            if (!pair || cur.Contains('\n') || prev.Contains('\n'))
            {
                continue;
            }

            var merged = prev + " " + cur;
            if (lines[i - 1].Column + merged.Length > MaxCompactWidth
                || !SameTokens(Tokenize(prev + "\n" + cur), Tokenize(merged)))
            {
                continue;
            }

            lines[i - 1].Text.Append(' ').Append(cur);
            lines.RemoveAt(i);
        }
    }

    private static string FirstWordLower(string text)
    {
        var end = 0;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        return text[..end].ToLowerInvariant();
    }

    // "group"/"order"/"partition" followed by "by": emit as one upper-cased clause
    // keyword. Only GROUP BY / ORDER BY act as clause breaks; PARTITION BY sits inside
    // an OVER(...) and is handled generically, so it's excluded here.
    private static bool TryTwoWordClause(List<Tok> tokens, int i, out string text, out int advance)
    {
        text = "";
        advance = 0;
        var lower = tokens[i].Text.ToLowerInvariant();
        if (lower is not ("group" or "order"))
        {
            return false;
        }

        var j = NextSignificant(tokens, i + 1);
        if (j < 0 || tokens[j].Kind != Kind.Word || !tokens[j].Text.Equals("by", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        text = lower == "group" ? "GROUP BY" : "ORDER BY";
        advance = j - i; // consume the intervening tokens (there are none but be safe) plus "by"
        return true;
    }

    private static bool IsSubqueryOpen(List<Tok> tokens, int openIndex)
    {
        var j = NextSignificant(tokens, openIndex + 1);
        if (j < 0 || tokens[j].Kind != Kind.Word)
        {
            return false;
        }

        var w = tokens[j].Text.ToLowerInvariant();
        return w is "select" or "with";
    }

    // A "(" hugs the token before it (no space) when that token names something
    // callable/subscriptable — an unquoted non-keyword identifier, a quoted
    // identifier, or a preceding ")". After a keyword ("in (", "values (") it gets a space.
    private static bool IsCallTarget(Tok prev) => prev.Kind switch
    {
        Kind.QuotedId or Kind.CloseParen => true,
        Kind.Word => !Keywords.Contains(prev.Text.ToLowerInvariant()),
        _ => false,
    };

    private static int NextSignificant(List<Tok> tokens, int from)
    {
        for (var i = from; i < tokens.Count; i++)
        {
            if (tokens[i].Kind is not (Kind.LineComment or Kind.BlockComment))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Upper(string text, string? lower) =>
        lower is not null && Keywords.Contains(lower) ? lower.ToUpperInvariant() : text;

    // ---- Tokenizer --------------------------------------------------------

    // The formatter's tokens, read through the shared SqlLexer so the formatter
    // agrees with the splitter and completion on where every string, quoted
    // identifier, dollar quote and comment starts and ends. Its own scanner used
    // to disagree on a few, and each disagreement was a layout that changed
    // what the SQL meant: 1_000 came out as "1 _000" (the number 1 aliased
    // _000) and N'x' as "N 'x'". What this adds on top of the lexer is only the
    // formatter's own view of a token: a run of operator characters is one
    // operator ("->>", "::", "<="), a $1 parameter reads as a word, a bracket
    // is an operator, and ".5" glued to a name is a dot and a number.
    private static List<Tok> Tokenize(string sql)
    {
        var tokens = new List<Tok>();
        var spaceBefore = false;
        var lexed = SqlLexer.Tokenize(sql);
        for (var i = 0; i < lexed.Count; i++)
        {
            var token = lexed[i];
            var text = sql.Substring(token.Start, token.Length);
            switch (token.Kind)
            {
                case SqlTokenKind.Whitespace:
                    spaceBefore = true;
                    continue;
                case SqlTokenKind.LineComment:
                    Add(Kind.LineComment, text.TrimEnd());
                    continue;
                case SqlTokenKind.BlockComment:
                    Add(Kind.BlockComment, text);
                    continue;
                case SqlTokenKind.Word or SqlTokenKind.Parameter:
                    Add(Kind.Word, text);
                    continue;
                case SqlTokenKind.QuotedIdentifier:
                    Add(Kind.QuotedId, text);
                    continue;
                case SqlTokenKind.String:
                    Add(Kind.Str, text);
                    continue;
                case SqlTokenKind.DollarString:
                    Add(Kind.Dollar, text);
                    continue;
                case SqlTokenKind.Number when text[0] == '.' && i > 0 && lexed[i - 1].End == token.Start
                                              && lexed[i - 1].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier:
                    // "t.5" — a name's member, not the number .5.
                    Add(Kind.Dot, ".");
                    Add(Kind.Number, text[1..]);
                    continue;
                case SqlTokenKind.Number:
                    Add(Kind.Number, text);
                    continue;
                case SqlTokenKind.Comma:
                    Add(Kind.Comma, ",");
                    continue;
                case SqlTokenKind.OpenParen:
                    Add(Kind.OpenParen, "(");
                    continue;
                case SqlTokenKind.CloseParen:
                    Add(Kind.CloseParen, ")");
                    continue;
                case SqlTokenKind.Semicolon:
                    Add(Kind.Semicolon, ";");
                    continue;
                case SqlTokenKind.Dot:
                    Add(Kind.Dot, ".");
                    continue;
                case SqlTokenKind.Operator or SqlTokenKind.DoubleColon:
                {
                    // One operator per run of operator characters, as the server reads them.
                    var end = token.End;
                    while (i + 1 < lexed.Count && lexed[i + 1].Start == end
                           && lexed[i + 1].Kind is SqlTokenKind.Operator or SqlTokenKind.DoubleColon)
                    {
                        i++;
                        end = lexed[i].End;
                    }

                    Add(Kind.Op, sql[token.Start..end]);
                    continue;
                }

                case SqlTokenKind.Other when text == "$" && i + 1 < lexed.Count && lexed[i + 1].Start == token.End
                                             && lexed[i + 1].Kind is SqlTokenKind.Word or SqlTokenKind.Number:
                    // A stray "$name" stays one word, as it always has.
                    i++;
                    Add(Kind.Word, sql[token.Start..lexed[i].End]);
                    continue;
                default:
                    // Brackets and anything the lexer has no name for: one-character operators.
                    Add(Kind.Op, text);
                    continue;
            }
        }

        return tokens;

        void Add(Kind kind, string text)
        {
            tokens.Add(new Tok(kind, text, spaceBefore));
            spaceBefore = false;
        }
    }

    // ---- Round-trip check -------------------------------------------------

    private static bool SameTokens(List<Tok> a, List<Tok> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind)
            {
                return false;
            }

            // Keywords/identifiers only changed case; literals and comments must be identical.
            var comparison = a[i].Kind == Kind.Word
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(a[i].Text, b[i].Text, comparison))
            {
                return false;
            }
        }

        return true;
    }

    // ---- Keyword tables ---------------------------------------------------

    // Clause keywords that start a fresh line right-aligned to the river.
    private static readonly HashSet<string> MajorBreak = new(StringComparer.Ordinal)
    {
        "select", "from", "where", "having", "limit", "offset", "values", "set",
        "returning", "window", "with", "union", "intersect", "except", "fetch",
        "insert", "update", "delete",
    };

    // The subset of clause keywords whose items break one-per-line beneath them.
    private static readonly HashSet<string> BodyForcing = new(StringComparer.Ordinal)
    {
        "select", "where", "having", "set", "returning", "window", "values",
    };

    private static readonly HashSet<string> JoinModifiers = new(StringComparer.Ordinal)
    {
        "inner", "left", "right", "full", "cross", "natural", "outer",
    };

    // Reserved words that get upper-cased and that "(" treats as non-callable
    // (so "in (" keeps its space while "count(" doesn't). Deliberately excludes
    // type names and function names, which keep whatever case the user wrote.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "select", "from", "where", "and", "or", "not", "in", "is", "null",
        "like", "ilike", "similar", "between", "exists", "any", "all", "some",
        "join", "inner", "left", "right", "full", "outer", "cross", "natural",
        "on", "using", "group", "by", "order", "having", "limit", "offset",
        "union", "intersect", "except", "distinct", "as", "asc", "desc",
        "insert", "into", "values", "update", "set", "delete", "returning",
        "with", "recursive", "case", "when", "then", "else", "end", "cast",
        "create", "table", "view", "materialized", "index", "sequence", "drop",
        "alter", "add", "column", "rename", "to", "primary", "key", "foreign",
        "references", "default", "unique", "check", "constraint", "cascade",
        "true", "false", "over", "partition", "window", "filter", "within",
        "fetch", "first", "next", "last", "rows", "row", "only", "lateral",
        "tablesample", "for", "of", "nulls", "grouping", "sets", "rollup",
        "cube", "if", "conflict", "do", "nothing", "unbounded", "preceding",
        "following", "current", "range", "groups", "ties",
    };
}
