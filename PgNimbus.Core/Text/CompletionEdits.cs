namespace PgNimbus.Core.Text;

/// <summary>How an accepted completion item is written into the buffer.</summary>
public enum CompletionInsertKind
{
    /// <summary>The insert text, as is.</summary>
    Plain,
    /// <summary>A callable: <c>name(</c>…<c>)</c>, reusing a <c>(</c> that is already there.</summary>
    Function,
    /// <summary>A relation, which may take an auto-alias in FROM/JOIN position.</summary>
    Table,
}

/// <summary>
/// One text edit: replace <see cref="ReplaceLength"/> characters at
/// <see cref="ReplaceStart"/> with <see cref="InsertText"/>, then put the caret
/// at <see cref="CaretOffset"/> (an absolute offset in the edited text).
/// </summary>
public readonly record struct CompletionEdit(int ReplaceStart, int ReplaceLength, string InsertText, int CaretOffset);

/// <summary>
/// Where the identifier under the caret starts and ends. <see cref="FilterStart"/>
/// is where the text the popup filters on begins — after the opening quote of a
/// quoted identifier, so <c>"Or</c> filters on <c>Or</c>. The replacement range
/// covers the whole token, quote included, and runs <i>past</i> the caret over
/// the rest of the word: accepting inside <c>cust|omer_id</c> replaces the
/// word rather than leaving <c>omer_id</c> dangling after the insert.
/// </summary>
public readonly record struct CompletionToken(int FilterStart, int ReplaceStart, int ReplaceEnd);

/// <summary>
/// What accepting a completion does to the text, decided in one place and as
/// one edit, so the editor can apply it as a single undo step. It used to be
/// three: the list's own replace of just the filter segment, a
/// <c>"()"</c>-caret nudge, and an auto-alias posted to the dispatcher a frame
/// later (so Undo took the alias off first, and a quick tab switch could land
/// the alias in a different document).
/// </summary>
public static class CompletionEdits
{
    /// <summary>The identifier token the caret sits in (or at the end of).</summary>
    public static CompletionToken TokenAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);

        var context = SqlCompletionContext.GetCaretContext(text, caret);
        if (context.InQuotedIdentifier)
        {
            var open = SqlLexer.Tokenize(text, 0, caret)[^1];
            var filterStart = text.IndexOf('"', open.Start) + 1;
            return new CompletionToken(filterStart, open.Start, QuotedEnd(text, caret));
        }

        var start = caret;
        while (start > 0 && SqlLexer.IsIdentPart(text[start - 1]))
        {
            start--;
        }

        var end = caret;
        while (end < text.Length && SqlLexer.IsIdentPart(text[end]))
        {
            end++;
        }

        return new CompletionToken(start, start, end);
    }

    // The rest of an open quoted identifier to the right of the caret: up to
    // and including its closing quote (the auto-closed one, typically), never
    // past the end of the line.
    private static int QuotedEnd(string text, int caret)
    {
        var i = caret;
        while (i < text.Length && text[i] != '\n')
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return caret;
    }

    /// <summary>
    /// The edit for accepting <paramref name="insertText"/> at
    /// <paramref name="caret"/>. <paramref name="aliasSeed"/> (a table's bare
    /// name) asks for an auto-alias; it is added only in FROM/JOIN position,
    /// and only when no alias already follows the replaced token.
    /// </summary>
    public static CompletionEdit Plan(string text, int caret, string insertText, CompletionInsertKind kind, string? aliasSeed = null)
    {
        var token = TokenAt(text, caret);
        var start = token.ReplaceStart;
        var length = token.ReplaceEnd - token.ReplaceStart;

        if (kind == CompletionInsertKind.Function)
        {
            var name = insertText.EndsWith("()", StringComparison.Ordinal) ? insertText[..^2] : insertText;
            // A "(" already there is the call's own — reuse it rather than
            // writing a second pair in front of it.
            return token.ReplaceEnd < text.Length && text[token.ReplaceEnd] == '('
                ? new CompletionEdit(start, length, name, start + name.Length + 1)
                : new CompletionEdit(start, length, name + "()", start + name.Length + 1);
        }

        if (kind == CompletionInsertKind.Table && aliasSeed is not null
            && AliasFor(text, token, aliasSeed) is { } alias)
        {
            var withAlias = $"{insertText} {alias}";
            return new CompletionEdit(start, length, withAlias, start + withAlias.Length);
        }

        return new CompletionEdit(start, length, insertText, start + insertText.Length);
    }

    // The auto-alias for a table accepted at `token`, or null when one would be
    // wrong: outside FROM/JOIN (an INSERT INTO / TRUNCATE target can't take a
    // bare alias), or when the user already wrote one after the table.
    private static string? AliasFor(string text, CompletionToken token, string seed)
    {
        var clause = SqlCompletionContext.GetCaretContext(text, token.ReplaceStart).Clause;
        if (clause is not (SqlClause.FromTableRef or SqlClause.JoinTableRef) || AliasFollows(text, token.ReplaceEnd))
        {
            return null;
        }

        var (stmtStart, stmtEnd) = SqlCompletionContext.CompletionStatementSpan(text, token.ReplaceStart);
        // The token being replaced is not a name the statement uses yet.
        var statement = string.Concat(
            text.AsSpan(stmtStart, token.ReplaceStart - stmtStart),
            new string(' ', token.ReplaceEnd - token.ReplaceStart),
            text.AsSpan(token.ReplaceEnd, stmtEnd - token.ReplaceEnd));

        var taken = new List<string>();
        foreach (var table in SqlCompletionContext.ExtractTables(statement))
        {
            taken.Add(table.Table);
            if (table.Alias is not null)
            {
                taken.Add(table.Alias);
            }
        }

        taken.AddRange(SqlCompletionContext.ExtractCteNames(statement));
        return TableAliaser.Derive(seed, taken);
    }

    // True when the next word after `end` (same line) is an alias — AS, or an
    // identifier that isn't a keyword a table reference can be followed by.
    private static bool AliasFollows(string text, int end)
    {
        var i = end;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        if (i == end && i < text.Length && text[i] != '\r' && text[i] != '\n')
        {
            return false; // glued to something else ("users," / "users)")
        }

        if (i < text.Length && text[i] == '"')
        {
            return true;
        }

        var wordStart = i;
        while (i < text.Length && SqlLexer.IsIdentPart(text[i]))
        {
            i++;
        }

        if (i == wordStart)
        {
            return false;
        }

        var word = SqlLexer.FoldCase(text.AsSpan(wordStart, i - wordStart));
        return word == "as" || !NotAnAlias.Contains(word);
    }

    // Words that can directly follow a table reference without being its alias.
    private static readonly HashSet<string> NotAnAlias = new(StringComparer.Ordinal)
    {
        "on", "using", "where", "group", "order", "having", "limit", "offset", "fetch",
        "join", "inner", "left", "right", "full", "outer", "cross", "natural", "lateral",
        "union", "intersect", "except", "returning", "window", "for", "tablesample",
        "set", "values", "select", "default", "overriding", "only",
    };
}
