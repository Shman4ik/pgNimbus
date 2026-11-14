using PgNimbus.Core.Text;

namespace PgNimbus.Core.Query;

/// <summary>A <c>[Start, Start + Length)</c> span of UTF-16 offsets in a text.</summary>
public readonly record struct SqlErrorSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Turns a server error's <see cref="QueryError.Position"/> into a place in the
/// editor. PostgreSQL reports the position 1-based, in characters (code points)
/// of the statement text it received; the editor counts UTF-16 units from the
/// start of the whole document. The mapping is therefore two steps: code points
/// to UTF-16 inside the sent statement, then the offset at which that statement
/// starts in the document. Pure, so it is tested where the rest of the text
/// handling is.
/// </summary>
public static class SqlErrorLocation
{
    /// <summary>
    /// The span to mark for a server position inside <paramref name="statement"/>,
    /// shifted by <paramref name="statementOffset"/> (where the statement starts in
    /// the document). The span is the lexer token the position falls in, or one
    /// character when it falls in whitespace. A position one past the end (the
    /// server's "at end of input") marks the statement's last token. Null for no
    /// position, a position below 1 or beyond that, or an empty statement.
    /// </summary>
    public static SqlErrorSpan? Map(string statement, int? position, int statementOffset = 0)
    {
        if (position is not { } p || p < 1 || string.IsNullOrEmpty(statement))
        {
            return null;
        }

        if (CodePointToUtf16(statement, p) is not { } offset)
        {
            return null;
        }

        // "At end of input": nothing is there to mark, so mark what came last.
        if (offset >= statement.Length)
        {
            offset = statement.Length - 1;
            if (offset > 0 && char.IsLowSurrogate(statement[offset]))
            {
                offset--;
            }
        }

        var span = TokenSpanAt(statement, offset);
        return new SqlErrorSpan(statementOffset + span.Start, span.Length);
    }

    /// <summary>
    /// <see cref="Map"/> for the statement a run actually sent: statement
    /// <paramref name="statementIndex"/> (0-based) of <paramref name="executedSql"/> as
    /// <see cref="SqlScriptSplitter.SplitSpans"/> splits it (trimmed, which is the
    /// text the engine sends), where <paramref name="executedSql"/> itself starts at
    /// <paramref name="executedOffset"/> in the document (0 for a whole-tab run, the
    /// selection's start for a selection run). Null when there is no such statement.
    /// </summary>
    public static SqlErrorSpan? MapInScript(string executedSql, int executedOffset, int statementIndex, int? position)
    {
        if (position is null)
        {
            return null;
        }

        var spans = SqlScriptSplitter.SplitSpans(executedSql);
        if (statementIndex < 0 || statementIndex >= spans.Count)
        {
            return null;
        }

        var (start, end) = spans[statementIndex];
        return Map(executedSql[start..end], position, executedOffset + start);
    }

    /// <summary>
    /// The UTF-16 offset of the <paramref name="position"/>-th code point (1-based)
    /// of <paramref name="text"/>; <c>text.Length</c> for one past the last; null
    /// for anything further or below 1. A lone surrogate counts as one character,
    /// as the server would never have received one.
    /// </summary>
    public static int? CodePointToUtf16(string text, int position)
    {
        if (position < 1)
        {
            return null;
        }

        var remaining = position - 1;
        var i = 0;
        while (remaining > 0 && i < text.Length)
        {
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            remaining--;
        }

        return remaining == 0 ? i : null;
    }

    /// <summary>
    /// The 1-based line and column of <paramref name="offset"/> in
    /// <paramref name="text"/>. Lines break at <c>\n</c> (so CRLF and LF read the
    /// same) and at a lone <c>\r</c>; the column counts characters (code points),
    /// as a reader would. Reads only the text before the offset.
    /// </summary>
    public static (int Line, int Column) LineColumn(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            var c = text[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        var column = 1;
        for (var i = lineStart; i < offset; i++)
        {
            if (!(char.IsLowSurrogate(text[i]) && i > lineStart && char.IsHighSurrogate(text[i - 1])))
            {
                column++;
            }
        }

        return (line, column);
    }

    /// <summary>
    /// The non-trivia token containing <paramref name="offset"/>, or a single
    /// character (both halves of a surrogate pair) when the offset sits in
    /// whitespace or a comment, where a whole token would mark too much.
    /// </summary>
    public static SqlErrorSpan TokenSpanAt(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));
        foreach (var token in SqlLexer.Tokenize(text))
        {
            if (offset >= token.Start && offset < token.End)
            {
                if (!token.IsTrivia && token.Length > 0)
                {
                    return new SqlErrorSpan(token.Start, token.Length);
                }

                break;
            }
        }

        var length = offset + 1 < text.Length && char.IsHighSurrogate(text[offset]) && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
        return new SqlErrorSpan(offset, Math.Min(length, Math.Max(0, text.Length - offset)));
    }
}
