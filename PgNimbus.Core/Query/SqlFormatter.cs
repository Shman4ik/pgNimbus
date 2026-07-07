using System.Text;

namespace PgNimbus.Core.Query;

/// <summary>
/// A pragmatic, dependency-free SQL pretty-printer: it lays a statement out in a
/// readable block style — each major clause on its own line, select-list and
/// SET/GROUP BY/ORDER BY items and JOINs broken one-per-line, <c>AND</c>/<c>OR</c>
/// predicates stacked, subqueries indented — and upper-cases reserved keywords.
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
    private const string IndentUnit = "    ";

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

        var formatted = Layout(tokens);

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

    private static string Layout(List<Tok> tokens)
    {
        var sb = new StringBuilder();

        var indent = 0;             // clause-keyword indentation (grows inside subqueries)
        var bodyIndent = 1;         // indentation of the current clause's items
        var bodyActive = false;     // are we in a clause whose items/AND/OR break one-per-line?
        var clauseParenDepth = 0;   // paren depth the current clause lives at
        var parenDepth = 0;
        var prevJoinMod = false;    // last emitted token was a JOIN modifier (LEFT/INNER/…)
        string? prevKeyword = null; // last keyword emitted, lower-cased (for DELETE FROM etc.)
        var lineStart = true;       // sb is empty or sits right after a newline+indent
        var pendingBreakIndent = -1; // >=0 ⇒ start the next token on a fresh line at this indent
        var curLineIndent = 0;      // indent of the line currently being built
        var prevKind = Kind.Semicolon; // last emitted token, for suppressing the following space
        var prevText = "";

        // Per-open-paren saved clause state, restored on the matching close.
        var stack = new Stack<(int Indent, int BodyIndent, bool BodyActive, int ClauseParenDepth, bool PrevJoinMod, string? PrevKeyword, int OpenLineIndent, bool Subquery)>();

        void Break(int level) => pendingBreakIndent = level;

        void Emit(string text, Kind kind, bool spaceBefore)
        {
            if (pendingBreakIndent >= 0 && sb.Length > 0)
            {
                sb.Append('\n');
                for (var k = 0; k < pendingBreakIndent; k++)
                {
                    sb.Append(IndentUnit);
                }

                curLineIndent = pendingBreakIndent;
                lineStart = true;
            }

            pendingBreakIndent = -1;

            // A "." , "(" , or "::" glues to whatever follows it, whatever the token asks.
            var space = spaceBefore
                && prevKind != Kind.Dot
                && prevKind != Kind.OpenParen
                && !(prevKind == Kind.Op && prevText == "::");
            if (!lineStart && space)
            {
                sb.Append(' ');
            }

            sb.Append(text);
            lineStart = false;
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
                    Break(indent);
                    Emit(clauseText, Kind.Word, spaceBefore: true);
                    bodyActive = true;
                    bodyIndent = indent + 1;
                    clauseParenDepth = parenDepth;
                    prevKeyword = clauseText.ToLowerInvariant();
                    prevJoinMod = false;
                    Break(bodyIndent);
                    i += advance;
                    break;

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
                        Break(indent);
                        Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                        clauseParenDepth = parenDepth;
                        if (BodyForcing.Contains(lower!))
                        {
                            bodyActive = true;
                            bodyIndent = indent + 1;
                            Break(bodyIndent);
                        }
                        else
                        {
                            bodyActive = false;
                        }
                    }

                    prevKeyword = lower;
                    prevJoinMod = false;
                    break;

                case Kind.Word when atClause && JoinModifiers.Contains(lower!):
                    if (!prevJoinMod)
                    {
                        Break(bodyActive ? bodyIndent : indent + 1);
                    }

                    Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                    prevJoinMod = true;
                    prevKeyword = lower;
                    break;

                case Kind.Word when atClause && lower == "join":
                    if (!prevJoinMod)
                    {
                        Break(bodyActive ? bodyIndent : indent + 1);
                    }

                    Emit("JOIN", Kind.Word, spaceBefore: true);
                    prevJoinMod = false;
                    prevKeyword = lower;
                    break;

                case Kind.Word when atClause && bodyActive && (lower == "and" || lower == "or"):
                    Break(bodyIndent);
                    Emit(Upper(t.Text, lower), Kind.Word, spaceBefore: true);
                    prevJoinMod = false;
                    prevKeyword = lower;
                    break;

                case Kind.Word:
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
                        Break(bodyIndent);
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
                    stack.Push((indent, bodyIndent, bodyActive, clauseParenDepth, prevJoinMod, prevKeyword, curLineIndent, subquery));

                    Emit("(", Kind.OpenParen, spaceBefore: !funcCall);
                    parenDepth++;

                    if (subquery)
                    {
                        indent = curLineIndent + 1;
                        bodyActive = false;
                        clauseParenDepth = parenDepth;
                        Break(indent);
                    }

                    prevJoinMod = false;
                    break;
                }

                case Kind.CloseParen:
                {
                    parenDepth = Math.Max(0, parenDepth - 1);
                    if (stack.Count > 0)
                    {
                        var s = stack.Pop();
                        indent = s.Indent;
                        bodyIndent = s.BodyIndent;
                        bodyActive = s.BodyActive;
                        clauseParenDepth = s.ClauseParenDepth;
                        prevJoinMod = s.PrevJoinMod;
                        prevKeyword = s.PrevKeyword;
                        if (s.Subquery)
                        {
                            Break(s.OpenLineIndent); // ")" lines up under the "(" that opened it
                        }
                    }

                    Emit(")", Kind.CloseParen, spaceBefore: false);
                    break;
                }

                case Kind.Semicolon:
                    Emit(";", Kind.Semicolon, spaceBefore: false);
                    // Reset clause state for a following statement.
                    indent = 0;
                    bodyActive = false;
                    clauseParenDepth = parenDepth;
                    prevKeyword = null;
                    prevJoinMod = false;
                    if (i < tokens.Count - 1)
                    {
                        Break(0);
                    }

                    break;

                case Kind.Dot:
                    Emit(".", Kind.Dot, spaceBefore: false);
                    prevJoinMod = false;
                    break;

                case Kind.LineComment:
                    Emit(t.Text, Kind.LineComment, spaceBefore: true);
                    Break(bodyActive ? bodyIndent : indent); // a line comment runs to EOL
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

        return sb.ToString();
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

    private static List<Tok> Tokenize(string sql)
    {
        var tokens = new List<Tok>();
        var n = sql.Length;
        var i = 0;
        var spaceBefore = false; // whitespace was skipped just before the next token

        void Add(Kind kind, string text)
        {
            tokens.Add(new Tok(kind, text, spaceBefore));
            spaceBefore = false;
        }

        while (i < n)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                spaceBefore = true;
                i++;
                continue;
            }

            // Prefixed string/identifier literals must stay glued to their prefix,
            // or a layout space would change their meaning (E'\n' ≠ E '\n'):
            //   E'…'/e'…' escape (backslash-aware), B'…'/X'…' bit/hex, U&'…'/U&"…" unicode.
            if (c is 'E' or 'e' or 'B' or 'b' or 'X' or 'x' && i + 1 < n && sql[i + 1] == '\'')
            {
                var end = c is 'E' or 'e' ? SkipEscapeQuoted(sql, i + 1) : SkipQuoted(sql, i + 1, '\'');
                Add(Kind.Str, sql[i..end]);
                i = end;
                continue;
            }

            if (c is 'U' or 'u' && i + 2 < n && sql[i + 1] == '&' && sql[i + 2] is '\'' or '"')
            {
                var quote = sql[i + 2];
                var end = SkipQuoted(sql, i + 2, quote);
                Add(quote == '"' ? Kind.QuotedId : Kind.Str, sql[i..end]);
                i = end;
                continue;
            }

            switch (c)
            {
                case '-' when i + 1 < n && sql[i + 1] == '-':
                {
                    var end = SkipLineComment(sql, i);
                    Add(Kind.LineComment, sql[i..end].TrimEnd());
                    i = end;
                    break;
                }

                case '/' when i + 1 < n && sql[i + 1] == '*':
                {
                    var end = SkipBlockComment(sql, i);
                    Add(Kind.BlockComment, sql[i..end]);
                    i = end;
                    break;
                }

                case '\'':
                {
                    var end = SkipQuoted(sql, i, '\'');
                    Add(Kind.Str, sql[i..end]);
                    i = end;
                    break;
                }

                case '"':
                {
                    var end = SkipQuoted(sql, i, '"');
                    Add(Kind.QuotedId, sql[i..end]);
                    i = end;
                    break;
                }

                case '$':
                {
                    var end = SkipDollarQuote(sql, i);
                    if (end > i)
                    {
                        Add(Kind.Dollar, sql[i..end]);
                        i = end;
                    }
                    else
                    {
                        // "$1" positional parameter or a stray "$": read as a word.
                        var w = i + 1;
                        while (w < n && (char.IsLetterOrDigit(sql[w]) || sql[w] == '_'))
                        {
                            w++;
                        }

                        Add(Kind.Word, sql[i..w]);
                        i = w;
                    }

                    break;
                }

                case ',':
                    Add(Kind.Comma, ",");
                    i++;
                    break;
                case '(':
                    Add(Kind.OpenParen, "(");
                    i++;
                    break;
                case ')':
                    Add(Kind.CloseParen, ")");
                    i++;
                    break;
                case ';':
                    Add(Kind.Semicolon, ";");
                    i++;
                    break;

                case '.' when !(i + 1 < n && char.IsAsciiDigit(sql[i + 1]) && (i == 0 || !IsWordChar(sql[i - 1]))):
                    Add(Kind.Dot, ".");
                    i++;
                    break;

                default:
                    if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(sql[i + 1])))
                    {
                        var end = ReadNumber(sql, i);
                        Add(Kind.Number, sql[i..end]);
                        i = end;
                    }
                    else if (char.IsLetter(c) || c == '_')
                    {
                        var end = i + 1;
                        while (end < n && IsWordChar(sql[end]))
                        {
                            end++;
                        }

                        Add(Kind.Word, sql[i..end]);
                        i = end;
                    }
                    else if (OpChars.IndexOf(c) >= 0)
                    {
                        var end = i + 1;
                        while (end < n && OpChars.IndexOf(sql[end]) >= 0)
                        {
                            end++;
                        }

                        Add(Kind.Op, sql[i..end]);
                        i = end;
                    }
                    else
                    {
                        // Anything else (brackets, braces, …) as a single-char op.
                        Add(Kind.Op, sql[i].ToString());
                        i++;
                    }

                    break;
            }
        }

        return tokens;
    }

    private static int ReadNumber(string sql, int i)
    {
        var n = sql.Length;
        var j = i;
        while (j < n && char.IsAsciiDigit(sql[j]))
        {
            j++;
        }

        if (j < n && sql[j] == '.' && j + 1 < n && char.IsAsciiDigit(sql[j + 1]))
        {
            j += 2;
            while (j < n && char.IsAsciiDigit(sql[j]))
            {
                j++;
            }
        }

        if (j < n && (sql[j] == 'e' || sql[j] == 'E'))
        {
            var k = j + 1;
            if (k < n && (sql[k] == '+' || sql[k] == '-'))
            {
                k++;
            }

            if (k < n && char.IsAsciiDigit(sql[k]))
            {
                j = k;
                while (j < n && char.IsAsciiDigit(sql[j]))
                {
                    j++;
                }
            }
        }

        return j;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private const string OpChars = "+-*/<>=~!@#%^&|:";

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

    // ---- Lexer skips (mirrors SqlScriptSplitter) --------------------------

    private static int SkipQuoted(string sql, int i, char quote)
    {
        var n = sql.Length;
        var j = i + 1;
        while (j < n)
        {
            if (sql[j] == quote)
            {
                if (j + 1 < n && sql[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return n;
    }

    // Like SkipQuoted for single quotes but honouring backslash escapes, as an
    // E'…' escape string uses them (so a "\'" is not a terminator).
    private static int SkipEscapeQuoted(string sql, int quoteIndex)
    {
        var n = sql.Length;
        var j = quoteIndex + 1;
        while (j < n)
        {
            var ch = sql[j];
            if (ch == '\\' && j + 1 < n)
            {
                j += 2;
                continue;
            }

            if (ch == '\'')
            {
                if (j + 1 < n && sql[j + 1] == '\'')
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return n;
    }

    private static int SkipLineComment(string sql, int i)
    {
        var n = sql.Length;
        var j = i + 2;
        while (j < n && sql[j] != '\n')
        {
            j++;
        }

        return j;
    }

    private static int SkipBlockComment(string sql, int i)
    {
        var n = sql.Length;
        var j = i + 2;
        var depth = 1;
        while (j < n && depth > 0)
        {
            if (j + 1 < n && sql[j] == '/' && sql[j + 1] == '*')
            {
                depth++;
                j += 2;
            }
            else if (j + 1 < n && sql[j] == '*' && sql[j + 1] == '/')
            {
                depth--;
                j += 2;
            }
            else
            {
                j++;
            }
        }

        return j;
    }

    private static int SkipDollarQuote(string sql, int i)
    {
        var n = sql.Length;
        var j = i + 1;
        while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
        {
            j++;
        }

        if (j >= n || sql[j] != '$')
        {
            return i;
        }

        if (j > i + 1 && char.IsDigit(sql[i + 1]))
        {
            return i;
        }

        var tag = sql[i..(j + 1)];
        var close = sql.IndexOf(tag, j + 1, StringComparison.Ordinal);
        return close < 0 ? n : close + tag.Length;
    }

    // ---- Keyword tables ---------------------------------------------------

    // Clause keywords that start a fresh line at the clause indent.
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
