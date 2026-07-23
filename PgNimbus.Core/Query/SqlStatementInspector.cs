using System.Text.RegularExpressions;

namespace PgNimbus.Core.Query;

/// <summary>
/// Lightweight, lexical inspection of a single SQL statement — enough to tell a
/// data-modifying statement from a read so the App can note that an
/// <c>EXPLAIN ANALYZE</c> was rolled back. Deliberately not a full parser: it
/// strips leading comments/whitespace and looks at the leading keyword (and, for
/// a CTE, whether a data-modifying keyword appears at all).
/// </summary>
public static partial class SqlStatementInspector
{
    private static readonly string[] ModifyingKeywords = ["insert", "update", "delete", "merge"];

    /// <summary>
    /// True when the statement writes rows (INSERT/UPDATE/DELETE/MERGE, or a
    /// data-modifying CTE). Conservative on the CTE case: a WITH whose body
    /// mentions a write keyword counts, since the write could hide inside a CTE.
    /// </summary>
    public static bool IsDataModifying(string sql)
    {
        var stripped = StripLeading(sql);
        if (stripped.Length == 0)
        {
            return false;
        }

        var leading = LeadingWord(stripped);
        if (Array.Exists(ModifyingKeywords, k => k.Equals(leading, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // A data-modifying CTE: `WITH x AS (DELETE …) SELECT …` still writes.
        if (leading.Equals("with", StringComparison.OrdinalIgnoreCase))
        {
            return WriteKeywordRegex().IsMatch(stripped);
        }

        return false;
    }

    /// <summary>Drops leading whitespace and SQL comments so the first real keyword is visible.</summary>
    private static string StripLeading(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            var c = sql[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
            }
            else if (c == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                var newline = sql.IndexOf('\n', index);
                index = newline < 0 ? sql.Length : newline + 1;
            }
            else if (c == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        return sql[index..];
    }

    private static string LeadingWord(string sql)
    {
        var end = 0;
        while (end < sql.Length && (char.IsLetter(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        return sql[..end];
    }

    // Word-boundary match so a column named "updated_at" or a string doesn't false-trigger the
    // leading-keyword path; only used for the (already WITH-gated) CTE case.
    [GeneratedRegex(@"\b(insert|update|delete|merge)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WriteKeywordRegex();
}
