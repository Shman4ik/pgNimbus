namespace PgNimbus.Core.Text;

/// <summary>
/// The completion row Enter would take: its name (what the list filters on),
/// what accepting it writes, whether it is a keyword (a keyword differing
/// only in letter case is the same word), and whether it is a guess — a
/// column of a relation the statement doesn't name, offered from the whole
/// catalog because nothing narrower is known yet.
/// </summary>
public readonly record struct CompletionRow(string Name, string InsertText, bool IsKeyword, bool IsGuess = false);

/// <summary>
/// When Enter accepts the highlighted completion row
/// (docs/design/sql-completion-audit-2.md §6.1). Enter is also the key that
/// ends a line, so a row it takes has to be one the user asked for, and taking
/// it has to change something:
/// <list type="number">
/// <item>Accepting must change the text. A row whose name is already written
/// in full (<c>customer_id⏎</c>, <c>DESC⏎</c>, <c>true⏎</c> against
/// <c>TRUE</c>) changes nothing the user can want, so Enter is a newline.
/// Parens a callable would add, a table's auto-alias or schema don't count:
/// they are decoration on a name the user has finished — unless the row was
/// chosen, when a callable still gets its parens and another schema's table
/// its schema.</item>
/// <item>A row the user chose — Ctrl+Space, the arrows, the mouse — is
/// taken.</item>
/// <item>Where a new name is written (<see cref="SqlCompletionContext.IsNewNamePosition"/>)
/// nothing else is: <c>FROM customers c⏎</c> is an alias, not <c>CROSS</c>.</item>
/// <item>A guess (a catalog column with no source in the statement) isn't
/// either: <c>SELECT query⏎</c> must not become <c>query_string</c> from some
/// other table.</item>
/// <item>Otherwise the row is taken when what was typed is the start of its
/// name.</item>
/// </list>
/// Tab takes the highlighted row whatever this says.
/// </summary>
public static class CompletionAcceptance
{
    /// <param name="text">The document.</param>
    /// <param name="caret">The caret offset.</param>
    /// <param name="filterStart">Where the text the popup filters on starts (see <see cref="CompletionToken.FilterStart"/>).</param>
    /// <param name="row">The highlighted row.</param>
    /// <param name="chosen">The user picked the row: asked for the list with Ctrl+Space, or moved to the row.</param>
    public static bool EnterAccepts(string text, int caret, int filterStart, CompletionRow row, bool chosen)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        // Everything below reads only the statement under the caret: this runs
        // on every keystroke while the popup is open.
        var (start, end) = SqlCompletionContext.CompletionStatementSpan(text, caret);
        text = text[start..end];
        caret -= start;
        filterStart -= start;
        var token = CompletionEdits.TokenAt(text, caret);
        var written = text[token.ReplaceStart..token.ReplaceEnd];
        if (!ChangesText(written, row, chosen))
        {
            return false;
        }

        if (chosen)
        {
            return true;
        }

        if (row.IsGuess || SqlCompletionContext.IsNewNamePosition(text, caret))
        {
            return false;
        }

        filterStart = Math.Clamp(filterStart, 0, caret);
        var typed = text[filterStart..caret];
        return typed.Length > 0 && row.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when accepting <paramref name="row"/> over the token
    /// <paramref name="written"/> would change it. Letter case alone is no
    /// change for anything unquoted (Postgres folds it, and a keyword is the
    /// same keyword in any case). A row that wasn't chosen and whose name is
    /// exactly what was written is no change either, even when accepting would
    /// qualify it: the name was typed in full, and a schema the user didn't
    /// write is not what their Enter meant.
    /// </summary>
    public static bool ChangesText(string written, CompletionRow row, bool chosen)
    {
        if (Same(written, row.InsertText))
        {
            return false;
        }

        return chosen || !Same(written, row.Name);
    }

    private static bool Same(string written, string name) =>
        written.StartsWith('"')
            ? string.Equals(written, name, StringComparison.Ordinal)
            : string.Equals(written, name, StringComparison.OrdinalIgnoreCase);
}
