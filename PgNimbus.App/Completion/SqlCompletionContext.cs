using System.Text.RegularExpressions;

namespace PgNimbus.App.Completion;

/// <summary>
/// A lightweight, regex-based read of the SQL being edited — just enough to make
/// completion context-aware without pulling in a full parser. It answers the two
/// questions <see cref="SqlCompletionProvider"/> asks at the caret:
/// <list type="number">
/// <item>Is this a <c>qualifier.partial</c> member access, and if so what is the
/// qualifier (the alias/table/schema before the dot)?</item>
/// <item>Which tables — with their aliases — does the surrounding statement pull
/// <c>FROM</c> (and <c>JOIN</c>)?</item>
/// </list>
/// Both are heuristics: they handle the shapes real queries actually take
/// (schema-qualified names, <c>AS</c>/implicit aliases, comma and JOIN lists)
/// and quietly give up on the exotic (correlated subqueries, CTEs) rather than
/// guess wrong.
/// </summary>
internal static partial class SqlCompletionContext
{
    /// <summary>A table reference parsed out of a FROM/JOIN clause, with its alias if one was given.</summary>
    public readonly record struct TableRef(string Schema, string Table, string? Alias);

    /// <summary>
    /// If the caret sits in the member position of a <c>qualifier.partial</c>
    /// expression (e.g. the caret in <c>u.na|</c> or right after <c>u.|</c>),
    /// returns the qualifier — the alias/table/schema immediately before the dot,
    /// unquoted. Returns null for a bare identifier with no dot to its left.
    /// </summary>
    public static string? GetQualifierBeforeCaret(string sql, int caret)
    {
        var i = Math.Clamp(caret, 0, sql.Length);

        // Skip back over the (possibly empty) word currently being typed.
        while (i > 0 && IsIdentPart(sql[i - 1]))
        {
            i--;
        }

        if (i == 0 || sql[i - 1] != '.')
        {
            return null;
        }

        // The identifier ending just before the dot is the qualifier.
        return ReadIdentifierBackward(sql, i - 1);
    }

    /// <summary>
    /// Extracts the table references from every FROM clause in <paramref name="sql"/>.
    /// Scoped to the FROM…(WHERE/GROUP/…) span so commas in a SELECT list aren't
    /// mistaken for table separators.
    /// </summary>
    public static IReadOnlyList<TableRef> ExtractTables(string sql)
    {
        var tables = new List<TableRef>();

        foreach (Match clause in FromClauseRegex().Matches(sql))
        {
            var body = clause.Groups["body"].Value;

            // A FROM body is a comma/JOIN-separated list of table refs; the ON
            // predicate trailing a JOIN stays glued to its table but is ignored
            // because SingleTableRefRegex is anchored at the segment start.
            foreach (var segment in JoinSplitRegex().Split(body))
            {
                var match = SingleTableRefRegex().Match(segment);
                if (!match.Success)
                {
                    continue;
                }

                var (schema, table) = SplitQualified(match.Groups["table"].Value);
                if (string.IsNullOrEmpty(table))
                {
                    continue;
                }

                var alias = match.Groups["alias"].Success ? Unquote(match.Groups["alias"].Value) : null;
                // A trailing keyword (ON, WHERE, …) can look like an alias — it isn't.
                if (alias is not null && ReservedAfterTable.Contains(alias))
                {
                    alias = null;
                }

                tables.Add(new TableRef(schema, table, alias));
            }
        }

        return tables;
    }

    // Reads the identifier that ends just before exclusive index `end` (walking
    // left), returning its unquoted text, or null if there's no identifier there.
    private static string? ReadIdentifierBackward(string sql, int end)
    {
        if (end <= 0)
        {
            return null;
        }

        if (sql[end - 1] == '"')
        {
            var open = end - 2;
            while (open >= 0 && sql[open] != '"')
            {
                open--;
            }

            return open < 0 ? null : sql.Substring(open + 1, end - 1 - (open + 1)).Replace("\"\"", "\"");
        }

        var start = end;
        while (start > 0 && IsIdentPart(sql[start - 1]))
        {
            start--;
        }

        return start == end ? null : sql.Substring(start, end - start);
    }

    private static (string Schema, string Table) SplitQualified(string raw)
    {
        var trimmed = raw.Trim();

        // First dot that isn't inside a quoted section separates schema from table.
        var inQuote = false;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == '.' && !inQuote)
            {
                return (Unquote(trimmed[..i]), Unquote(trimmed[(i + 1)..]));
            }
        }

        return ("", Unquote(trimmed));
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"'
            ? s[1..^1].Replace("\"\"", "\"")
            : s;
    }

    private static bool IsIdentPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '$';

    // Words that can legally follow a table reference but are never an alias, so
    // they must not be swallowed as one.
    private static readonly HashSet<string> ReservedAfterTable = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "using", "where", "group", "order", "having", "limit", "offset",
        "join", "inner", "left", "right", "full", "outer", "cross", "natural",
        "and", "or", "union", "intersect", "except", "returning", "window",
        "for", "as", "tablesample",
    };

    // FROM (or a comma/JOIN list under it), captured up to the next top-level
    // clause keyword or statement end. [\s\S] so the body spans newlines.
    [GeneratedRegex(
        @"\bfrom\b(?<body>[\s\S]*?)(?=\b(?:where|group|having|order|limit|offset|union|intersect|except|returning|window|for|into|values|set)\b|;|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromClauseRegex();

    // Splits a FROM body into individual table-ref segments on commas and any
    // flavour of JOIN (INNER/LEFT/RIGHT/FULL/CROSS/NATURAL/OUTER).
    [GeneratedRegex(
        @",|\b(?:cross\s+|natural\s+|inner\s+|left\s+|right\s+|full\s+|outer\s+)*join\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JoinSplitRegex();

    // A single table ref at the start of a segment: an optionally schema-qualified,
    // optionally quoted name, then an optional (AS) alias.
    [GeneratedRegex(
        """^\s*(?<table>(?:"[^"]+"|[\w$]+)(?:\s*\.\s*(?:"[^"]+"|[\w$]+))?)(?:\s+(?:as\s+)?(?<alias>"[^"]+"|[\w$]+))?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleTableRefRegex();
}
