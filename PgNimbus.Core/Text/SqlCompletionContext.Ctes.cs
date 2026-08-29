namespace PgNimbus.Core.Text;

public static partial class SqlCompletionContext
{
    /// <summary>
    /// A CTE with the output columns completion could derive for it.
    /// <paramref name="Columns"/> is what the CTE's declared column list or its
    /// SELECT list names (expressions without an alias are skipped rather than
    /// guessed); <paramref name="SelectsStar"/> is true when the list contains a
    /// <c>*</c> / <c>t.*</c>, in which case the columns of
    /// <paramref name="SourceTables"/> (its FROM/JOIN tables — each possibly
    /// another CTE) complete the picture on the provider side, where the
    /// catalog lives.
    /// </summary>
    public readonly record struct CteDefinition(
        string Name,
        IReadOnlyList<string> Columns,
        bool SelectsStar,
        IReadOnlyList<TableRef> SourceTables);

    /// <summary>
    /// The CTEs a <c>WITH</c> clause introduces, each with the output columns
    /// derivable from its declared column list (<c>WITH x (a, b) AS …</c>) or,
    /// failing that, its body's top-level SELECT list. Same heuristic spirit as
    /// the rest of this class: handles the shapes people actually write, gives
    /// up quietly (empty columns) on the rest.
    /// </summary>
    public static IReadOnlyList<CteDefinition> ExtractCteDefinitions(string sql)
    {
        var masked = MaskCommentsAndStrings(sql);
        var defs = new List<CteDefinition>();

        foreach (System.Text.RegularExpressions.Match match in CteNameRegex().Matches(masked))
        {
            var name = Unquote(match.Groups["name"].Value);
            // The regex ends at the body's opening paren; find its balanced close.
            var bodyStart = match.Index + match.Length;
            var body = masked[bodyStart..FindBalancedClose(masked, bodyStart)];

            List<string> columns;
            var selectsStar = false;
            if (match.Groups["cols"].Success)
            {
                // WITH x (a, b) AS (…) — the declared list *is* the output shape.
                var declared = match.Groups["cols"].Value.Trim();
                columns = [.. SplitTopLevel(declared[1..^1])
                    .Select(c => Unquote(c.Trim()))
                    .Where(c => c.Length > 0)];
            }
            else
            {
                columns = DeriveSelectListColumns(body, out selectsStar);
            }

            defs.Add(new CteDefinition(name, columns, selectsStar, ExtractTables(body)));
        }

        return defs;
    }

    // The output column names a body's top-level SELECT list yields: aliases
    // (explicit AS or implicit), the last segment of a plain dotted reference,
    // nothing for an unaliased expression. For a recursive/UNION body only the
    // first branch is read — that's the one that names the columns anyway.
    private static List<string> DeriveSelectListColumns(string body, out bool selectsStar)
    {
        selectsStar = false;
        var columns = new List<string>();
        if (FindSelectListSpan(body) is not var (start, end) || start >= end)
        {
            return columns;
        }

        foreach (var raw in SplitTopLevel(body[start..end]))
        {
            var item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            if (item == "*" || item.EndsWith(".*", StringComparison.Ordinal))
            {
                selectsStar = true;
                continue;
            }

            if (DeriveItemName(item) is { } column)
            {
                columns.Add(column);
            }
        }

        return columns;
    }

    // Locates the span between the body's first top-level SELECT (past its
    // ALL/DISTINCT [ON (…)] quantifier) and the clause keyword that ends the
    // list. Null when the body has no top-level SELECT (e.g. a VALUES CTE).
    private static (int Start, int End)? FindSelectListSpan(string body)
    {
        var i = 0;
        var depth = 0;
        int? start = null;

        while (i < body.Length)
        {
            var c = body[i];
            if (c == '"')
            {
                var close = body.IndexOf('"', i + 1);
                i = close < 0 ? body.Length : close + 1;
                continue;
            }

            if (c == '(' || c == '[')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')' || c == ']')
            {
                depth--;
                i++;
                continue;
            }

            if (IsIdentPart(c))
            {
                var wordStart = i;
                while (i < body.Length && IsIdentPart(body[i]))
                {
                    i++;
                }

                if (depth == 0 && !char.IsAsciiDigit(c))
                {
                    var word = body.AsSpan(wordStart, i - wordStart);
                    if (start is null)
                    {
                        if (word.Equals("select", StringComparison.OrdinalIgnoreCase))
                        {
                            SkipSelectQuantifier(body, ref i);
                            start = i;
                        }
                    }
                    else if (IsSelectListStop(word))
                    {
                        return (start.Value, wordStart);
                    }
                }

                continue;
            }

            i++;
        }

        return start is null ? null : (start.Value, body.Length);
    }

    // Advances past SELECT's ALL / DISTINCT / DISTINCT ON (…) quantifier so the
    // list span starts at the first actual item.
    private static void SkipSelectQuantifier(string body, ref int i)
    {
        var j = i;
        SkipWhitespace(body, ref j);
        var word = ReadWord(body, ref j);
        if (word.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            i = j;
            return;
        }

        if (!word.Equals("distinct", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        i = j;
        SkipWhitespace(body, ref j);
        if (!ReadWord(body, ref j).Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SkipWhitespace(body, ref j);
        if (j >= body.Length || body[j] != '(')
        {
            // "DISTINCT on_hand, …" — that ON-looking word was an item, and
            // `i` already sits right before it.
            return;
        }

        var depth = 0;
        while (j < body.Length)
        {
            var c = body[j];
            if (c == '"')
            {
                var close = body.IndexOf('"', j + 1);
                j = close < 0 ? body.Length : close + 1;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                j++;
                break;
            }

            j++;
        }

        i = j;
    }

    private static bool IsSelectListStop(ReadOnlySpan<char> word) =>
        word.Equals("from", StringComparison.OrdinalIgnoreCase)
        || word.Equals("where", StringComparison.OrdinalIgnoreCase)
        || word.Equals("group", StringComparison.OrdinalIgnoreCase)
        || word.Equals("having", StringComparison.OrdinalIgnoreCase)
        || word.Equals("order", StringComparison.OrdinalIgnoreCase)
        || word.Equals("limit", StringComparison.OrdinalIgnoreCase)
        || word.Equals("offset", StringComparison.OrdinalIgnoreCase)
        || word.Equals("fetch", StringComparison.OrdinalIgnoreCase)
        || word.Equals("union", StringComparison.OrdinalIgnoreCase)
        || word.Equals("intersect", StringComparison.OrdinalIgnoreCase)
        || word.Equals("except", StringComparison.OrdinalIgnoreCase)
        || word.Equals("window", StringComparison.OrdinalIgnoreCase)
        || word.Equals("into", StringComparison.OrdinalIgnoreCase);

    // The output name of one (trimmed) SELECT-list item, or null when the item
    // is an expression whose name we can't know without executing it:
    //   "total"            → total          (bare reference)
    //   "o.total"          → total          (dotted reference, pure chain only)
    //   "count(*) AS cnt"  → cnt            (explicit alias)
    //   "count(*) cnt"     → cnt            (implicit alias)
    //   "price * qty"      → null           (unaliased expression)
    //   "CASE … END"       → null           (END is never an alias)
    private static string? DeriveItemName(string item)
    {
        var end = item.Length;
        while (end > 0 && char.IsWhiteSpace(item[end - 1]))
        {
            end--;
        }

        if (end == 0)
        {
            return null;
        }

        // The trailing token, quoted or bare — anything else (')', ']', an
        // operator) means the item doesn't end in an identifier at all.
        int tokenStart;
        var quoted = item[end - 1] == '"';
        if (quoted)
        {
            tokenStart = item.LastIndexOf('"', end - 2);
            if (tokenStart < 0)
            {
                return null;
            }
        }
        else if (IsIdentPart(item[end - 1]))
        {
            tokenStart = end;
            while (tokenStart > 0 && IsIdentPart(item[tokenStart - 1]))
            {
                tokenStart--;
            }

            if (char.IsAsciiDigit(item[tokenStart]))
            {
                return null; // a numeric literal, not an identifier
            }
        }
        else
        {
            return null;
        }

        var token = item[tokenStart..end];
        if (!quoted && AliasStopWords.Contains(token))
        {
            return null; // "CASE … END", "x IS NOT NULL", … — expression tail, not an alias
        }

        var p = tokenStart;
        var hadSpace = false;
        while (p > 0 && char.IsWhiteSpace(item[p - 1]))
        {
            p--;
            hadSpace = true;
        }

        if (p == 0)
        {
            return Unquote(token); // the whole item is this one identifier
        }

        if (item[p - 1] == '.')
        {
            if (hadSpace)
            {
                return null;
            }

            // "o.total" names total; "t.a + u.b" names nothing — only a pure
            // qualifier chain back to the item start counts as a reference.
            var q = p - 1;
            while (q > 0 && (IsIdentPart(item[q - 1]) || item[q - 1] == '"' || item[q - 1] == '.'))
            {
                q--;
            }

            return q == 0 ? Unquote(token) : null;
        }

        if (!hadSpace)
        {
            return null;
        }

        // "<something> alias": the something must *end* like a value — another
        // identifier (covers the AS keyword too), a close paren/bracket, or a
        // quoted identifier. An operator there means the item is still one
        // unaliased expression.
        var prev = item[p - 1];
        return prev == ')' || prev == ']' || prev == '"' || IsIdentPart(prev)
            ? Unquote(token)
            : null;
    }

    // Words that can legally *end* a SELECT-list expression but are never its
    // alias — seeing one as the trailing token means "unaliased expression".
    private static readonly HashSet<string> AliasStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "end", "null", "true", "false", "not", "and", "or", "is", "in",
        "like", "ilike", "similar", "between", "asc", "desc", "over",
        "filter", "within", "escape", "collate", "interval", "at", "time",
        "zone", "then", "else", "when", "case", "distinct", "all", "as",
    };

    // Index of the ')' that closes an open paren group starting just past
    // `start` (depth already 1), or the string's length when unterminated —
    // a half-typed CTE body still yields whatever columns it has so far.
    private static int FindBalancedClose(string s, int start)
    {
        var depth = 1;
        var i = start;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                var close = s.IndexOf('"', i + 1);
                i = close < 0 ? s.Length : close + 1;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }

            i++;
        }

        return s.Length;
    }

    // Splits on the commas at paren/bracket depth 0 — the separators between
    // SELECT-list items or declared column names, not the ones inside calls.
    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                var close = s.IndexOf('"', i + 1);
                i = close < 0 ? s.Length : close + 1;
                continue;
            }

            if (c == '(' || c == '[')
            {
                depth++;
            }
            else if (c == ')' || c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }

            i++;
        }

        parts.Add(s[start..]);
        return parts;
    }

    private static ReadOnlySpan<char> ReadWord(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && IsIdentPart(s[i]))
        {
            i++;
        }

        return s.AsSpan(start, i - start);
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
    }
}
