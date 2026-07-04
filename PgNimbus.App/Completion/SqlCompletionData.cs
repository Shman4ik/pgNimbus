using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace PgNimbus.App.Completion;

public sealed class SqlCompletionData : ICompletionData
{
    public SqlCompletionData(string text, string description)
    {
        Text = text;
        Description = description;
    }

    public IImage? Image => null;

    public string Text { get; }

    public object Content => Text;

    public object Description { get; }

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
