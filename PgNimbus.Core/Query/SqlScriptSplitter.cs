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
/// This is a lexer, not a parser: it understands only enough Postgres syntax to
/// find the semicolons that actually separate statements. It assumes
/// <c>standard_conforming_strings</c> (the default since Postgres 9.1), so a
/// backslash inside a <c>'…'</c> literal is an ordinary character and only a
/// doubled quote escapes; <c>E'…'</c> escape strings are not special-cased.
/// </remarks>
public static class SqlScriptSplitter
{
    public static IReadOnlyList<string> Split(string sql)
    {
        var statements = new List<string>();
        foreach (var (start, end) in RawSpans(sql))
        {
            var text = sql[start..end].Trim();
            if (text.Length > 0)
            {
                statements.Add(text);
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
            var text = sql[start..end].Trim();
            if (text.Length == 0)
            {
                continue;
            }

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

    // Raw, untrimmed [start, end) spans between semicolons - the lexical scan
    // both Split and StatementAt key off. Respects single-quoted string
    // literals ('' escapes), double-quoted identifiers, dollar-quoted strings
    // ($tag$...$tag$), line comments (-- to end of line), and (nestable)
    // block comments, per the type-level remarks.
    private static List<(int Start, int End)> RawSpans(string sql)
    {
        var spans = new List<(int, int)>();
        if (string.IsNullOrEmpty(sql))
        {
            return spans;
        }

        var n = sql.Length;
        var start = 0;
        var i = 0;

        while (i < n)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'':
                case '"':
                    i = SkipQuoted(sql, i, c);
                    break;
                case '-' when i + 1 < n && sql[i + 1] == '-':
                    i = SkipLineComment(sql, i);
                    break;
                case '/' when i + 1 < n && sql[i + 1] == '*':
                    i = SkipBlockComment(sql, i);
                    break;
                case '$':
                    var afterDollar = SkipDollarQuote(sql, i);
                    // Not a dollar-quote open (e.g. a `$1` positional parameter
                    // or a stray `$`): step over just this character.
                    i = afterDollar > i ? afterDollar : i + 1;
                    break;
                case ';':
                    spans.Add((start, i));
                    start = i + 1;
                    i = start;
                    break;
                default:
                    i++;
                    break;
            }
        }

        // Trailing statement with no closing semicolon.
        spans.Add((start, n));
        return spans;
    }

    // Returns the index just past the closing quote (or end-of-string if the
    // literal is unterminated). A doubled quote (`''` / `""`) is an escape, not a
    // close.
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

    // Postgres block comments nest, so `/* /* */ */` is a single comment.
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

    // If `sql[i]` opens a dollar-quoted string, returns the index just past its
    // closing tag (or end-of-string if unterminated). Otherwise returns `i` to
    // signal "not a dollar quote" so the caller advances by one character.
    private static int SkipDollarQuote(string sql, int i)
    {
        var n = sql.Length;

        // Read the opening tag: $tag$ where tag is empty or an identifier that
        // does not start with a digit (that would be a `$1`-style parameter).
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

        var tag = sql[i..(j + 1)];   // includes both delimiting '$' characters
        var searchFrom = j + 1;
        var close = sql.IndexOf(tag, searchFrom, StringComparison.Ordinal);
        return close < 0 ? n : close + tag.Length;
    }
}
