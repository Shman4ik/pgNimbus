namespace PgNimbus.Core.Text;

/// <summary>
/// Where the statements of a document under edit begin, remembered across edits,
/// so the statement around the caret is found by lexing from the last known
/// boundary before it instead of from the top of the document.
/// </summary>
/// <remarks>
/// The editor's per-keystroke readers (the caret's clause, the argument hint, the
/// Enter rule) each find their statement with
/// <see cref="SqlCompletionContext.CompletionStatementSpan"/>, which lexes the whole
/// document, and several of them run on one key. In a 5 MB dump that was a lex of
/// 5 MB per reader per keystroke, and with the argument hint open, per caret move.
/// A boundary is the offset just past a top-level <c>;</c>, where the lexer is back in
/// its default state; an edit changes nothing before its own offset, so every
/// boundary at or before it stays true and only the ones after it are dropped.
/// <see cref="Span"/> answers exactly what <c>CompletionStatementSpan</c> does
/// (a generative test holds it to that).
/// </remarks>
public sealed class SqlStatementBoundaries
{
    // Offsets just past a top-level ';', ascending. Every boundary before
    // _scannedTo is in the list.
    private readonly List<int> _boundaries = [];
    private int _scannedTo;

    /// <summary>Forgets what an edit at <paramref name="offset"/> may have changed: the boundaries after it.</summary>
    public void Invalidate(int offset)
    {
        offset = Math.Max(0, offset);
        if (offset >= _scannedTo)
        {
            return;
        }

        _scannedTo = offset;
        var keep = _boundaries.Count;
        while (keep > 0 && _boundaries[keep - 1] > offset)
        {
            keep--;
        }

        _boundaries.RemoveRange(keep, _boundaries.Count - keep);
    }

    /// <summary>Forgets everything, for a document replaced wholesale.</summary>
    public void Reset()
    {
        _boundaries.Clear();
        _scannedTo = 0;
    }

    /// <summary>
    /// The statement around <paramref name="caret"/> in <paramref name="sql"/>, which
    /// must be the document as edited so far (every edit since the last call passed
    /// to <see cref="Invalidate"/>): the same span as
    /// <see cref="SqlCompletionContext.CompletionStatementSpan"/>.
    /// </summary>
    public (int Start, int End) Span(string sql, int caret)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var start = Start(sql, caret);
        return (start, End(sql, start, caret));
    }

    // The last boundary at or before the caret, lexing only from the last one known.
    private int Start(string sql, int caret)
    {
        _scannedTo = Math.Min(_scannedTo, sql.Length);
        if (caret > _scannedTo)
        {
            var from = _boundaries.Count > 0 ? _boundaries[^1] : 0;
            foreach (var token in SqlLexer.Tokenize(sql, from, caret))
            {
                if (token.Kind == SqlTokenKind.Semicolon && token.End <= caret)
                {
                    _boundaries.Add(token.End);
                }
            }

            _scannedTo = caret;
        }

        var index = _boundaries.BinarySearch(caret);
        if (index >= 0)
        {
            return _boundaries[index];
        }

        index = ~index;
        return index > 0 ? _boundaries[index - 1] : 0;
    }

    // Where the statement ends: the start of the first ';' ending past the caret,
    // else the end of the document. Lexed forward from the statement's start in
    // growing windows, so the rest of a long document is only read when the
    // statement really runs to its end. A window cuts only its last token; any
    // ';' found before that is as the whole document would lex it.
    private static int End(string sql, int start, int caret)
    {
        var window = Math.Max(4096, (caret - start) * 2);
        while (true)
        {
            var end = (int)Math.Min(sql.Length, (long)caret + window);
            foreach (var token in SqlLexer.Tokenize(sql, start, end))
            {
                if (token.Kind == SqlTokenKind.Semicolon && token.End > caret)
                {
                    return token.Start;
                }
            }

            if (end == sql.Length)
            {
                return sql.Length;
            }

            window *= 2;
        }
    }
}
