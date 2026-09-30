using PgNimbus.Core.Text;

namespace PgNimbus.Core.Query;

/// <summary>
/// Splits a SQL script into its individual top-level statements on semicolons,
/// while respecting the lexical contexts where a bare split would break: single-
/// quoted string literals (<c>''</c> escapes), double-quoted identifiers,
/// dollar-quoted strings (<c>$tag$…$tag$</c>), line comments (<c>--</c> to end
/// of line), and (nestable) block comments. Statement text is returned trimmed;
/// empty statements — including runs of whitespace or comments between
/// semicolons — are dropped, so an editor full of only comments yields nothing.
/// </summary>
/// <remarks>
/// Not a parser: it splits on the <see cref="SqlTokenKind.Semicolon"/> tokens
/// of the shared <see cref="SqlLexer"/>, so it assumes what the lexer assumes —
/// <c>standard_conforming_strings</c> on (the default since Postgres 9.1) for
/// plain <c>'…'</c> literals, backslash escapes inside <c>E'…'</c>.
/// </remarks>
public static class SqlScriptSplitter
{
    public static IReadOnlyList<string> Split(string sql)
    {
        var statements = new List<string>();
        foreach (var (start, end) in RawSpans(sql))
        {
            if (HasSignificantText(sql, start, end))
            {
                statements.Add(sql[start..end].Trim());
            }
        }

        return statements;
    }

    /// <summary>
    /// Finds the statement that <paramref name="offset"/> (a caret position
    /// into <paramref name="sql"/>) sits in - the same semicolon-delimited
    /// unit <see cref="Split"/> would produce for it - so "run just this
    /// statement" execution doesn't require selecting it first. An offset
    /// sitting in a blank/comment-only gap between statements resolves to the
    /// next statement, or the previous one if there is none after it. Returns
    /// null only when <paramref name="sql"/> has no statement at all.
    /// </summary>
    public static string? StatementAt(string sql, int offset)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return null;
        }

        offset = Math.Clamp(offset, 0, sql.Length);

        string? before = null;
        foreach (var (start, end) in RawSpans(sql))
        {
            if (!HasSignificantText(sql, start, end))
            {
                continue;
            }

            var text = sql[start..end].Trim();
            if (offset >= start && offset <= end)
            {
                return text;
            }

            if (start > offset)
            {
                return text;
            }

            before = text;
        }

        return before;
    }

    /// <summary>
    /// Like <see cref="StatementAt"/>, but returns the trimmed statement's
    /// <c>[Start, End)</c> character span in <paramref name="sql"/> instead of its
    /// text, so a caller can replace exactly that region (e.g. format-in-place).
    /// Uses the same gap-resolution rule and returns null only when there is no
    /// statement at all.
    /// </summary>
    public static (int Start, int End)? StatementSpanAt(string sql, int offset)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return null;
        }

        offset = Math.Clamp(offset, 0, sql.Length);

        (int, int)? before = null;
        foreach (var (start, end) in RawSpans(sql))
        {
            if (!HasSignificantText(sql, start, end) || TrimSpan(sql, start, end) is not { } span)
            {
                continue;
            }

            if (offset >= start && offset <= end)
            {
                return span;
            }

            if (start > offset)
            {
                return span;
            }

            before = span;
        }

        return before;
    }

    // True when the span contains anything besides whitespace and comments —
    // i.e. text worth returning as a statement. This is what makes comment-only
    // segments count as gaps rather than statements, per the type-level summary.
    private static bool HasSignificantText(string sql, int start, int end)
    {
        foreach (var token in SqlLexer.Tokenize(sql, start, end))
        {
            if (!token.IsTrivia)
            {
                return true;
            }
        }

        return false;
    }

    // Narrows a raw [start, end) span to the non-whitespace text inside it,
    // returning null when the span holds only whitespace.
    private static (int Start, int End)? TrimSpan(string sql, int start, int end)
    {
        var s = start;
        while (s < end && char.IsWhiteSpace(sql[s]))
        {
            s++;
        }

        var e = end;
        while (e > s && char.IsWhiteSpace(sql[e - 1]))
        {
            e--;
        }

        return e > s ? (s, e) : null;
    }

    // Raw, untrimmed [start, end) spans between semicolons - the lexical scan
    // both Split and StatementAt key off. The shared SqlLexer decides what a
    // literal, quoted identifier or comment is, so a ';' inside any of them
    // (E'…' escapes and $tag1$…$tag1$ included) never splits.
    private static List<(int Start, int End)> RawSpans(string sql)
    {
        var spans = new List<(int, int)>();
        if (string.IsNullOrEmpty(sql))
        {
            return spans;
        }

        var start = 0;
        foreach (var token in SqlLexer.Tokenize(sql))
        {
            if (token.Kind == SqlTokenKind.Semicolon)
            {
                spans.Add((start, token.Start));
                start = token.End;
            }
        }

        // Trailing statement with no closing semicolon.
        spans.Add((start, sql.Length));
        return spans;
    }
}
