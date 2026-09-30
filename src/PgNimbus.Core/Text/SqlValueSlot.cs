namespace PgNimbus.Core.Text;

/// <summary>
/// A place where the caret types a value rather than a name, and what decides
/// which values fit there (docs/dev/design/sql-completion-audit-2.md E07):
/// <list type="bullet">
/// <item>the right-hand side of a comparison with a column
/// (<c>i.status = |</c>, <c>status &lt;&gt; '|</c>, <c>status IN ('a', |</c>) —
/// <see cref="ComparedColumn"/> is that column's dotted name, folded like the
/// server folds it, so its type (an enum's labels, a boolean) can be looked up;</item>
/// <item>an argument of a call (<c>nextval('|</c>, <c>date_trunc('|</c>,
/// <c>extract(|</c>) — <see cref="Function"/> and <see cref="ArgumentIndex"/>.</item>
/// </list>
/// <see cref="InString"/> when the caret is inside the string literal that
/// holds the value, so a candidate is written without its quotes.
/// </summary>
public sealed record SqlValueSlot(bool InString, IReadOnlyList<string>? ComparedColumn, string? Function, int ArgumentIndex)
{
    // Operators after which the right-hand side is a value of the left-hand column's type.
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal) { "=", "<>", "!=" };

    /// <summary>
    /// The value slot at <paramref name="caret"/> in <paramref name="statement"/>,
    /// or null when the caret isn't typing a value the text around it can type:
    /// read from the tokens left of the word being typed, like the rest of
    /// completion, so it holds up on half-written text.
    /// </summary>
    public static SqlValueSlot? At(string statement, int caret)
    {
        caret = Math.Clamp(caret, 0, statement.Length);
        var wordStart = caret;
        while (wordStart > 0 && SqlLexer.IsIdentPart(statement[wordStart - 1]))
        {
            wordStart--;
        }

        var tokens = new List<SqlToken>();
        var lexed = SqlLexer.Tokenize(statement, 0, wordStart);
        // A line comment runs to the newline, which is never inside the lexed
        // range while the caret is still on the comment's line.
        if (lexed.Count > 0 && lexed[^1] is { Kind: SqlTokenKind.LineComment } or { Kind: SqlTokenKind.BlockComment, IsIncomplete: true })
        {
            return null;
        }

        foreach (var token in lexed)
        {
            if (!token.IsTrivia)
            {
                tokens.Add(token);
            }
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        var inString = false;
        var last = tokens[^1];
        if (last.IsProse && last.IsIncomplete)
        {
            // Only a plain string literal holds a value; a comment or a dollar
            // quote (a function body) does not.
            if (last.Kind != SqlTokenKind.String)
            {
                return null;
            }

            inString = true;
            tokens.RemoveAt(tokens.Count - 1);
            if (tokens.Count == 0)
            {
                return null;
            }

            last = tokens[^1];
        }
        else if (last.IsProse || last.Kind == SqlTokenKind.QuotedIdentifier && last.IsIncomplete)
        {
            return null;
        }

        if (last.Kind == SqlTokenKind.Operator)
        {
            // The lexer reads operator characters one at a time: "<>" is two
            // adjacent tokens.
            var first = tokens.Count - 1;
            while (first > 0 && tokens[first - 1].Kind == SqlTokenKind.Operator && tokens[first - 1].End == tokens[first].Start)
            {
                first--;
            }

            return Comparisons.Contains(statement[tokens[first].Start..last.End])
                && ColumnEndingAt(statement, tokens, first - 1) is { } column
                ? new SqlValueSlot(inString, column, null, -1)
                : null;
        }

        if (last.Kind is not (SqlTokenKind.OpenParen or SqlTokenKind.Comma))
        {
            return null;
        }

        // Inside "( … ,": which parentheses, and how many arguments before this one.
        var depth = 0;
        var commas = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.Kind is SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket)
            {
                depth++;
            }
            else if (token.Kind is SqlTokenKind.OpenBracket && depth > 0)
            {
                depth--;
            }
            else if (token.Kind == SqlTokenKind.OpenParen)
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                if (i == 0)
                {
                    return null;
                }

                var before = tokens[i - 1];
                var word = before.Kind == SqlTokenKind.Word ? SqlLexer.FoldCase(statement.AsSpan(before.Start, before.Length)) : null;
                if (word == "in")
                {
                    // "col IN (…", "col NOT IN (…"
                    var columnEnd = i - 2;
                    if (columnEnd >= 0 && tokens[columnEnd].Kind == SqlTokenKind.Word
                        && SqlLexer.FoldCase(statement.AsSpan(tokens[columnEnd].Start, tokens[columnEnd].Length)) == "not")
                    {
                        columnEnd--;
                    }

                    return ColumnEndingAt(statement, tokens, columnEnd) is { } column
                        ? new SqlValueSlot(inString, column, null, -1)
                        : null;
                }

                return before.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier && !before.IsIncomplete
                    ? new SqlValueSlot(inString, null, SqlLexer.IdentifierName(statement, before), commas)
                    : null;
            }
            else if (token.Kind == SqlTokenKind.Comma && depth == 0)
            {
                commas++;
            }
            else if (token.Kind == SqlTokenKind.Semicolon)
            {
                return null;
            }
        }

        return null;
    }

    // The dotted column name whose last part is tokens[end]: "status",
    // "i.status", "saas.issues.status". Null when tokens[end] isn't a name.
    private static IReadOnlyList<string>? ColumnEndingAt(string statement, List<SqlToken> tokens, int end)
    {
        if (end < 0 || tokens[end].Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier) || tokens[end].IsIncomplete)
        {
            return null;
        }

        var parts = new List<string> { SqlLexer.IdentifierName(statement, tokens[end]) };
        var i = end;
        while (parts.Count < 3 && i >= 2 && tokens[i - 1].Kind == SqlTokenKind.Dot
            && tokens[i - 2].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
        {
            parts.Insert(0, SqlLexer.IdentifierName(statement, tokens[i - 2]));
            i -= 2;
        }

        return parts;
    }
}
