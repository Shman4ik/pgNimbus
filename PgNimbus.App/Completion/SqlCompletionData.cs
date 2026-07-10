using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace PgNimbus.App.Completion;

public sealed class SqlCompletionData : ICompletionData
{
    /// <param name="text">The name shown in the list and matched against what the user typed.</param>
    /// <param name="description">The right-hand hint (keyword / schema / table / column).</param>
    /// <param name="insertText">
    /// What actually gets written when the item is accepted. Defaults to
    /// <paramref name="text"/>; a schema/table/column passes its quote-if-needed
    /// form (<c>"Spells"</c>) so the user filters on the bare name but inserts a
    /// spelling Postgres will resolve.
    /// </param>
    /// <param name="priority">
    /// Ranking hint the completion list uses to pre-select the best match among
    /// equally-good textual matches — higher wins. Lets context-aware
    /// completion float the current table's columns above the rest of the
    /// catalog (see <see cref="SqlCompletionProvider"/>).
    /// </param>
    public SqlCompletionData(string text, string description, string? insertText = null, double priority = 0)
    {
        Text = text;
        Description = description;
        InsertText = insertText ?? text;
        Priority = priority;
    }

    public IImage? Image => null;

    public string Text { get; }

    /// <summary>The literal inserted on completion — may be quoted even when <see cref="Text"/> isn't.</summary>
    public string InsertText { get; }

    /// <summary>
    /// The bare table name when this item completes a table — the seed for the
    /// auto-alias appended after an accept in FROM/JOIN position (see
    /// <c>MainWindow.MaybeInsertTableAlias</c>). Null for anything that isn't a
    /// table, which opts the item out of aliasing entirely.
    /// </summary>
    public string? AliasTable { get; init; }

    public object Content => Text;

    public object Description { get; }

    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, InsertText);

        // A function inserts as "name()" — land the caret between the parens so
        // the arguments can be typed straight away.
        if (InsertText.EndsWith("()", StringComparison.Ordinal))
        {
            textArea.Caret.Offset -= 1;
        }
    }
}
