using System.Text.RegularExpressions;

namespace PgNimbus.Core.Query;

/// <summary>
/// Lightweight, lexical inspection of a single SQL statement — enough to tell a
/// data-modifying statement from a read so the App can note that an
/// <c>EXPLAIN ANALYZE</c> was rolled back, to recognize/unwrap a hand-written
/// <c>EXPLAIN</c>, and to tell whether re-running a statement is provably harmless.
/// Deliberately not a full parser: it strips leading
/// comments/whitespace and looks at the leading keyword (and, for a CTE, whether a
/// data-modifying keyword appears at all).
/// </summary>
public static partial class SqlStatementInspector
{
    private static readonly string[] ModifyingKeywords = ["insert", "update", "delete", "merge"];

    // Leading keywords that produce a result set without writing. WITH is included but
    // still has to clear the data-modifying-CTE check; SHOW/TABLE/VALUES read only.
    private static readonly string[] ReadKeywords = ["select", "with", "table", "values", "show"];

    // The only options the pre-9.0 `EXPLAIN [ANALYZE] [VERBOSE] stmt` form accepts, in
    // the order it accepts them — everything else requires the parenthesized list.
    private static readonly string[] LegacyExplainKeywords = ["analyze", "verbose"];

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

    /// <summary>
    /// True when running the statement a second time is provably harmless: it reads,
    /// it writes nothing, and it calls nothing whose side effect would be applied
    /// twice. Used to decide whether a result set containing a column Npgsql can't
    /// materialize may be re-executed with those columns requested in text format
    /// (see <see cref="QueryEngine"/>'s text fallback).
    /// </summary>
    /// <remarks>
    /// Deliberately conservative, and lexical like the rest of this type — a false
    /// negative only costs the placeholder rendering of an unreadable cell, while a
    /// false positive would run a side effect twice. So: the statement must lead with
    /// a read keyword, must not be a data-modifying CTE, must not be <c>SELECT … INTO</c>
    /// (which creates a table), must not name a function known to have a side effect,
    /// and must be a single statement — Postgres's simple query protocol happily runs
    /// <c>SELECT 1; DROP TABLE t</c> as one command. A side-effecting name hiding inside
    /// a string literal or a comment is treated as if it were real, since being wrong
    /// in that direction is free.
    /// </remarks>
    public static bool IsSafeToReExecute(string sql)
    {
        var stripped = StripLeading(sql);
        if (stripped.Length == 0 || ContainsStatementSeparator(stripped))
        {
            return false;
        }

        var leading = LeadingWord(stripped);
        if (!Array.Exists(ReadKeywords, k => k.Equals(leading, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !IsDataModifying(stripped)
            && !SelectIntoRegex().IsMatch(stripped)
            && !SideEffectingCallRegex().IsMatch(stripped);
    }

    // A ';' with anything but whitespace/comments after it means a second statement
    // could ride along. Cheap and quote-blind on purpose: a ';' inside a literal just
    // makes the answer "no", which is the safe direction.
    private static bool ContainsStatementSeparator(string sql)
    {
        var semicolon = sql.IndexOf(';');
        return semicolon >= 0 && StripLeading(sql[(semicolon + 1)..]).Length > 0;
    }

    /// <summary>
    /// True when the statement is itself an <c>EXPLAIN</c> — i.e. it already produces a
    /// plan rather than a result set, so its output can be parsed back into a plan tree
    /// instead of being shown as raw <c>QUERY PLAN</c> text rows.
    /// </summary>
    public static bool IsExplain(string sql) =>
        LeadingWord(StripLeading(sql)).Equals("explain", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the statement an <c>EXPLAIN</c> wraps (<c>EXPLAIN (ANALYZE, BUFFERS)
    /// SELECT 1</c> → <c>SELECT 1</c>), or <paramref name="sql"/> unchanged when it isn't
    /// an EXPLAIN. Handles both the parenthesized option list and the legacy bare-keyword
    /// form (<c>EXPLAIN ANALYZE VERBOSE …</c> — the only two keywords that form accepts).
    /// Used so re-explaining an already-EXPLAINed statement produces one EXPLAIN rather
    /// than a nested pair, which Postgres rejects as a syntax error.
    /// </summary>
    public static string StripExplain(string sql)
    {
        var stripped = StripLeading(sql);
        if (!LeadingWord(stripped).Equals("explain", StringComparison.OrdinalIgnoreCase))
        {
            return sql;
        }

        var index = "explain".Length;
        index = SkipWhitespace(stripped, index);

        if (index < stripped.Length && stripped[index] == '(')
        {
            index = SkipOptionList(stripped, index);
        }
        else
        {
            // Legacy form: EXPLAIN [ ANALYZE ] [ VERBOSE ] statement.
            foreach (var keyword in LegacyExplainKeywords)
            {
                var after = SkipWhitespace(stripped, index);
                if (!LeadingWord(stripped[after..]).Equals(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                index = after + keyword.Length;
            }
        }

        var inner = stripped[SkipWhitespace(stripped, index)..].TrimEnd();
        // Refuse to hand back nothing: a bare "EXPLAIN" (or one whose option list never
        // closes) is a broken statement, and the caller's error is clearer than ours.
        return inner.Length == 0 ? sql : inner;
    }

    private static int SkipWhitespace(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Skips the parenthesized EXPLAIN option list, returning the index just past its
    /// closing paren (or end-of-string when unterminated). Counts nesting and steps over
    /// single-quoted values, so an option like <c>FORMAT 'json'</c> can't fool it.
    /// </summary>
    private static int SkipOptionList(string sql, int index)
    {
        var depth = 0;
        while (index < sql.Length)
        {
            var c = sql[index];
            if (c == '\'')
            {
                index++;
                while (index < sql.Length && sql[index] != '\'')
                {
                    index++;
                }
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }

            index++;
        }

        return index;
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

    // SELECT … INTO new_table creates a table; only INSERT's INTO is a write this
    // type already catches by its leading keyword. "into" is reserved, so it can't
    // be a bare column alias here.
    [GeneratedRegex(@"\binto\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelectIntoRegex();

    // Functions whose second call would be a second side effect: sequence advances,
    // session/transaction locks, replication-stream writes, backend signals, and
    // dblink's arbitrary remote execution. Not exhaustive — no lexical check can be,
    // since any user-defined VOLATILE function may write — which is why this gate
    // only ever permits an *extra* read, never suppresses the per-cell guard that
    // renders an unreadable value as a placeholder.
    [GeneratedRegex(
        @"\b(nextval|setval|pg_advisory_\w+|pg_logical_emit_message|pg_create_restore_point|pg_terminate_backend|pg_cancel_backend|dblink\w*)\s*\(",
        RegexOptions.IgnoreCase)]
    private static partial Regex SideEffectingCallRegex();
}
