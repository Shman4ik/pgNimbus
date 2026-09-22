using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Query;

/// <summary>
/// One condition of a parsed browse <c>WHERE</c>: a typed <see cref="Filter"/>
/// when the text is one the filter chips can express and edit, otherwise null,
/// in which case the condition is kept verbatim as <see cref="Text"/> (a "raw"
/// chip: shown, removable, never reinterpreted).
/// </summary>
public sealed record ParsedCondition(string Text, RowFilter? Filter);

/// <summary>The parts of a browse page query the browse view model is made of.</summary>
public sealed record BrowseQueryShape(
    IReadOnlyList<ParsedCondition> Conditions,
    string? SortColumn,
    bool SortDescending,
    int Limit,
    int Offset);

/// <summary>
/// Reads a page query of the shape browse mode writes back into its parts, so a
/// browse tab whose SQL someone edited by hand can stay a browse tab, with its
/// conditions as filter chips:
/// <code>SELECT * FROM table [WHERE …] [ORDER BY col [ASC|DESC] | pk…] LIMIT n [OFFSET m]</code>
/// Anything outside that shape — a column list, a join, GROUP BY, no LIMIT, a
/// different table — is not a browse query, and <see cref="TryParse"/> returns
/// null: the tab stays the plain query the user wrote.
///
/// The <c>WHERE</c> is split on its top-level <c>AND</c>s, and every part is
/// kept, so the chips always account for the whole clause: a part in the subset
/// <see cref="RowFilterSql"/> produces (<c>col op literal</c>, <c>IS [NOT] NULL</c>,
/// <c>IS TRUE/FALSE</c>, the <c>ILIKE</c> text searches) becomes a typed filter;
/// anything else (<c>OR</c>, functions, subqueries, <c>BETWEEN</c>, <c>IN</c>, …)
/// is kept verbatim as a raw condition. A typed filter re-renders to a predicate
/// with the same meaning (a bare <c>6</c> becomes the untyped literal <c>'6'</c>,
/// which Postgres types by the column) — parsing never guesses at an expression
/// it can't reproduce, it leaves it as text.
/// </summary>
public static class BrowseSqlParser
{
    public static BrowseQueryShape? TryParse(string sql, string schema, string table, IReadOnlyList<ColumnDetail> columns)
    {
        if (Tokenize(sql) is not { } tokens)
        {
            return null; // an unterminated string, identifier or comment
        }

        if (tokens.Count > 0 && tokens[^1].Kind == Kind.Semicolon)
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (tokens.Any(t => t.Kind == Kind.Semicolon))
        {
            return null; // more than one statement
        }

        var i = 0;
        if (!Keyword(tokens, i++, "select") || !(At(tokens, i++) is { Kind: Kind.Op, Text: "*" }) || !Keyword(tokens, i++, "from"))
        {
            return null;
        }

        // [schema .] table
        if (!Identifier(tokens, i, out var first))
        {
            return null;
        }

        i++;
        string? qualifier = null;
        var name = first;
        if (At(tokens, i) is { Kind: Kind.Dot })
        {
            if (!Identifier(tokens, i + 1, out var second))
            {
                return null;
            }

            qualifier = first;
            name = second;
            i += 2;
        }

        if (name != table || (qualifier is not null && qualifier != schema))
        {
            return null;
        }

        // WHERE … up to a top-level ORDER BY / LIMIT.
        var conditions = new List<ParsedCondition>();
        if (Keyword(tokens, i, "where"))
        {
            var start = ++i;
            var depth = 0;
            while (i < tokens.Count)
            {
                var t = tokens[i];
                depth += t.Kind == Kind.LParen ? 1 : t.Kind == Kind.RParen ? -1 : 0;
                if (depth == 0 && (Keyword(tokens, i, "order") || Keyword(tokens, i, "limit") || Keyword(tokens, i, "offset")))
                {
                    break;
                }

                if (depth == 0 && t.Kind == Kind.Word && t.Lower is "group" or "having" or "window" or "union"
                    or "except" or "intersect" or "for" or "fetch")
                {
                    return null;
                }

                i++;
            }

            if (i == start)
            {
                return null;
            }

            foreach (var part in SplitAnd(tokens, start, i))
            {
                conditions.Add(ParseCondition(sql, tokens, part.Start, part.End, columns));
            }
        }

        string? sortColumn = null;
        var descending = false;
        if (Keyword(tokens, i, "order"))
        {
            if (!Keyword(tokens, i + 1, "by"))
            {
                return null;
            }

            i += 2;
            var sortKeys = new List<(string Column, bool Desc)>();
            while (true)
            {
                if (!Identifier(tokens, i, out var column) || columns.All(c => c.Name != column))
                {
                    return null;
                }

                i++;
                var desc = false;
                if (Keyword(tokens, i, "asc"))
                {
                    i++;
                }
                else if (Keyword(tokens, i, "desc"))
                {
                    desc = true;
                    i++;
                }

                sortKeys.Add((column, desc));
                if (At(tokens, i) is { Kind: Kind.Comma })
                {
                    i++;
                    continue;
                }

                break;
            }

            var pk = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (sortKeys.Count == 1 && !(pk.Count == 1 && sortKeys[0].Column == pk[0] && !sortKeys[0].Desc))
            {
                sortColumn = sortKeys[0].Column;
                descending = sortKeys[0].Desc;
            }
            else if (!sortKeys.Select(k => k.Column).SequenceEqual(pk) || sortKeys.Any(k => k.Desc))
            {
                // A multi-column order other than the key's own: browse can't
                // hold it, so leave the query as the user's.
                return null;
            }
        }

        if (!Keyword(tokens, i, "limit") || !Integer(tokens, i + 1, out var limit) || limit <= 0)
        {
            return null;
        }

        i += 2;
        var offset = 0;
        if (Keyword(tokens, i, "offset"))
        {
            if (!Integer(tokens, i + 1, out offset))
            {
                return null;
            }

            i += 2;
        }

        return i == tokens.Count ? new BrowseQueryShape(conditions, sortColumn, descending, limit, offset) : null;
    }

    // ---- WHERE parts ----------------------------------------------------------

    // Top-level ANDs, not the one inside BETWEEN x AND y.
    private static IEnumerable<(int Start, int End)> SplitAnd(List<Token> tokens, int start, int end)
    {
        var depth = 0;
        var partStart = start;
        var pendingBetween = false;
        for (var i = start; i < end; i++)
        {
            var t = tokens[i];
            depth += t.Kind == Kind.LParen ? 1 : t.Kind == Kind.RParen ? -1 : 0;
            if (depth != 0 || t.Kind != Kind.Word)
            {
                continue;
            }

            if (t.Lower == "between")
            {
                pendingBetween = true;
            }
            else if (t.Lower == "and")
            {
                if (pendingBetween)
                {
                    pendingBetween = false;
                    continue;
                }

                yield return (partStart, i);
                partStart = i + 1;
            }
        }

        yield return (partStart, end);
    }

    private static ParsedCondition ParseCondition(string sql, List<Token> tokens, int start, int end, IReadOnlyList<ColumnDetail> columns)
    {
        // One layer of wrapping parentheses — what browse mode writes around
        // each part — when they enclose the whole part.
        while (end - start >= 2 && tokens[start].Kind == Kind.LParen && tokens[end - 1].Kind == Kind.RParen
               && MatchingParen(tokens, start) == end - 1)
        {
            start++;
            end--;
        }

        var text = sql[tokens[start].Start..tokens[end - 1].End];
        return new ParsedCondition(text, TryTyped(tokens.GetRange(start, end - start), columns));
    }

    private static RowFilter? TryTyped(List<Token> t, IReadOnlyList<ColumnDetail> columns)
    {
        if (!Identifier(t, 0, out var name) || columns.FirstOrDefault(c => c.Name == name) is not { } column)
        {
            return null;
        }

        var filter = MatchShape(t, name);
        if (filter is null)
        {
            return null;
        }

        // Only what the chip's own operator picker can show: a filter it can't
        // display would be a chip that can't be edited.
        return RowFilterSql.OperatorsFor(column.Editor, column.DataType).Contains(filter.Operator) ? filter : null;
    }

    private static RowFilter? MatchShape(List<Token> t, string column)
    {
        // col IS [NOT] NULL | col IS TRUE | col IS FALSE
        if (t.Count is 3 or 4 && Keyword(t, 1, "is"))
        {
            return (t.Count, t[2].Lower, At(t, 3)?.Lower) switch
            {
                (3, "null", _) => new RowFilter(column, FilterOperator.IsNull),
                (3, "true", _) => new RowFilter(column, FilterOperator.IsTrue),
                (3, "false", _) => new RowFilter(column, FilterOperator.IsFalse),
                (4, "not", "null") => new RowFilter(column, FilterOperator.IsNotNull),
                _ => null,
            };
        }

        // col op literal
        if (t.Count is 3 or 4 && t[1].Kind == Kind.Op && Comparison(t[1].Text) is { } op
            && Literal(t, 2, out var value, out var used) && 2 + used == t.Count)
        {
            return new RowFilter(column, op, value);
        }

        // col [::text] [NOT] ILIKE 'pattern'
        var i = 1;
        if (At(t, i) is { Kind: Kind.Op, Text: "::" } && Keyword(t, i + 1, "text"))
        {
            i += 2;
        }

        var negated = Keyword(t, i, "not");
        if (negated)
        {
            i++;
        }

        if (Keyword(t, i, "ilike") && At(t, i + 1) is { Kind: Kind.Str } pattern && i + 2 == t.Count
            && LikeSearch(Unquote(pattern.Text)) is { } search)
        {
            return search.Op switch
            {
                FilterOperator.Contains => new RowFilter(column, negated ? FilterOperator.NotContains : FilterOperator.Contains, search.Value),
                _ when negated => null,
                _ => new RowFilter(column, search.Op, search.Value),
            };
        }

        return null;
    }

    private static FilterOperator? Comparison(string op) => op switch
    {
        "=" => FilterOperator.Equals,
        "<>" or "!=" => FilterOperator.NotEquals,
        "<" => FilterOperator.Less,
        "<=" => FilterOperator.LessOrEqual,
        ">" => FilterOperator.Greater,
        ">=" => FilterOperator.GreaterOrEqual,
        _ => null,
    };

    // A plain string literal, a number, or a negated number; how many tokens it took.
    private static bool Literal(List<Token> t, int i, out string value, out int used)
    {
        value = string.Empty;
        used = 0;
        switch (At(t, i))
        {
            case { Kind: Kind.Str } s:
                value = Unquote(s.Text);
                used = 1;
                return true;
            case { Kind: Kind.Number } n:
                value = n.Text;
                used = 1;
                return true;
            case { Kind: Kind.Op, Text: "-" } when At(t, i + 1) is { Kind: Kind.Number } n:
                value = "-" + n.Text;
                used = 2;
                return true;
            default:
                return false;
        }
    }

    // '%x%' → contains x, 'x%' → starts with, '%x' → ends with — the shapes
    // RowFilterSql writes. Anything with a wildcard in the middle, an
    // unescaped '_', or no wildcard at all is a pattern, not a text search.
    private static (FilterOperator Op, string Value)? LikeSearch(string pattern)
    {
        var leading = pattern.StartsWith('%');
        var body = leading ? pattern[1..] : pattern;
        var trailing = body.EndsWith('%') && !EndsEscaped(body);
        if (trailing)
        {
            body = body[..^1];
        }

        if (!leading && !trailing)
        {
            return null;
        }

        var value = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\')
            {
                if (i + 1 >= body.Length)
                {
                    return null;
                }

                value.Append(body[++i]);
            }
            else if (c is '%' or '_')
            {
                return null;
            }
            else
            {
                value.Append(c);
            }
        }

        if (value.Length == 0)
        {
            return null;
        }

        var op = (leading, trailing) switch
        {
            (true, true) => FilterOperator.Contains,
            (false, true) => FilterOperator.StartsWith,
            _ => FilterOperator.EndsWith,
        };
        return (op, value.ToString());
    }

    // True when the final character is escaped by an odd run of backslashes.
    private static bool EndsEscaped(string s)
    {
        var backslashes = 0;
        for (var i = s.Length - 2; i >= 0 && s[i] == '\\'; i--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static string Unquote(string literal) => literal[1..^1].Replace("''", "'");

    // ---- Token helpers ----------------------------------------------------------

    private static Token? At(List<Token> tokens, int i) => i >= 0 && i < tokens.Count ? tokens[i] : null;

    private static bool Keyword(List<Token> tokens, int i, string word) =>
        At(tokens, i) is { Kind: Kind.Word } t && t.Lower == word;

    // A bare identifier (folded to lower case, as Postgres folds it) or a quoted one (verbatim).
    private static bool Identifier(List<Token> tokens, int i, out string name)
    {
        name = string.Empty;
        switch (At(tokens, i))
        {
            case { Kind: Kind.Word } w:
                name = w.Lower;
                return true;
            case { Kind: Kind.QuotedId } q:
                name = q.Text[1..^1].Replace("\"\"", "\"");
                return true;
            default:
                return false;
        }
    }

    private static bool Integer(List<Token> tokens, int i, out int value)
    {
        value = 0;
        return At(tokens, i) is { Kind: Kind.Number } n && int.TryParse(n.Text, out value);
    }

    private static int MatchingParen(List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            depth += tokens[i].Kind == Kind.LParen ? 1 : tokens[i].Kind == Kind.RParen ? -1 : 0;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- Tokenizer ---------------------------------------------------------------

    private enum Kind
    {
        Word,
        QuotedId,
        Str,
        OtherStr,
        Number,
        Op,
        LParen,
        RParen,
        Comma,
        Dot,
        Semicolon,
    }

    private sealed record Token(Kind Kind, string Text, int Start, int End)
    {
        public string Lower { get; } = Text.ToLowerInvariant();
    }

    // The browse parser's tokens, read through the shared SqlLexer so it agrees
    // with the splitter, completion and the formatter on where a string, a
    // quoted identifier, a dollar quote or a comment ends (its own scanner closed
    // a nested block comment at the first "*/"). Comments are dropped; every
    // token keeps its source span so a raw condition can be quoted back exactly
    // as written. Null when a literal, identifier or comment is still open: the
    // query isn't finished, so it isn't a browse query. Only what the parser can
    // reproduce gets a kind it reads: a plain '…' string is Str; every other
    // literal form (E'…', B'…', X'…', N'…', U&'…', a dollar quote), a U&"…"
    // identifier and a $1 parameter are OtherStr, which no typed filter accepts,
    // so a condition holding one stays a raw, verbatim chip.
    private static List<Token>? Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var lexed = SqlLexer.Tokenize(sql);
        for (var i = 0; i < lexed.Count; i++)
        {
            var token = lexed[i];
            if (token.IsIncomplete)
            {
                return null;
            }

            if (token.IsTrivia)
            {
                continue;
            }

            var start = token.Start;
            var end = token.End;
            Kind kind;
            switch (token.Kind)
            {
                case SqlTokenKind.Word:
                    kind = Kind.Word;
                    break;
                case SqlTokenKind.QuotedIdentifier:
                    kind = sql[start] == '"' ? Kind.QuotedId : Kind.OtherStr;
                    break;
                case SqlTokenKind.String:
                    kind = sql[start] == '\'' ? Kind.Str : Kind.OtherStr;
                    break;
                case SqlTokenKind.DollarString or SqlTokenKind.Parameter:
                    kind = Kind.OtherStr;
                    break;
                case SqlTokenKind.Number:
                    kind = Kind.Number;
                    break;
                case SqlTokenKind.OpenParen:
                    kind = Kind.LParen;
                    break;
                case SqlTokenKind.CloseParen:
                    kind = Kind.RParen;
                    break;
                case SqlTokenKind.Comma:
                    kind = Kind.Comma;
                    break;
                case SqlTokenKind.Dot:
                    kind = Kind.Dot;
                    break;
                case SqlTokenKind.Semicolon:
                    kind = Kind.Semicolon;
                    break;
                case SqlTokenKind.Operator when sql[start] != ':':
                    // One operator per run of operator characters ("<=", "<>"),
                    // a ':' ending the run so "col::text" still splits cleanly.
                    kind = Kind.Op;
                    while (i + 1 < lexed.Count && lexed[i + 1].Start == end
                           && lexed[i + 1].Kind == SqlTokenKind.Operator && sql[lexed[i + 1].Start] != ':')
                    {
                        end = lexed[++i].End;
                    }

                    break;
                default:
                    // "::", a lone ':', brackets, anything else: not part of a
                    // query browse mode writes, and never a typed filter's.
                    kind = Kind.Op;
                    break;
            }

            tokens.Add(new Token(kind, sql[start..end], start, end));
        }

        return tokens;
    }
}
