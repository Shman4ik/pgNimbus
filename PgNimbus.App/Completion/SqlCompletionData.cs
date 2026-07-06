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
    public SqlCompletionData(string text, string description, string? insertText = null)
    {
        Text = text;
        Description = description;
        InsertText = insertText ?? text;
    }

    public IImage? Image => null;

    public string Text { get; }

    /// <summary>The literal inserted on completion — may be quoted even when <see cref="Text"/> isn't.</summary>
    public string InsertText { get; }

    public object Content => Text;

    public object Description { get; }

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, InsertText);
}
