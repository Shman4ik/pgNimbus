using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace PgNimbus.App;

/// <summary>
/// Paints a subtle wash behind the bracket pair surrounding the caret.
/// AvaloniaEdit ships no bracket matching of its own, so the match is found
/// here: the character on either side of the caret is checked against
/// <c>()[]</c> and its partner located by nesting-aware scan. The scan is
/// over raw text (string literals and comments aren't excluded) — the same
/// trade-off most lightweight editors make, and wrong only inside a literal
/// that contains unbalanced brackets.
/// </summary>
public sealed class BracketHighlightRenderer : IBackgroundRenderer
{
    private const string Open = "([";
    private const string Close = ")]";

    private readonly TextView _textView;
    private int _firstOffset = -1;
    private int _secondOffset = -1;

    /// <summary>Theme-resolved highlight brush; set by the window alongside the syntax palette.</summary>
    public IBrush? Brush { get; set; }

    public BracketHighlightRenderer(TextView textView)
    {
        _textView = textView;
        textView.BackgroundRenderers.Add(this);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>
    /// Recomputes the highlighted pair for the given caret offset and repaints.
    /// Takes the live document (an <see cref="ITextSource"/>) rather than a
    /// string so caret movement doesn't allocate a full-document copy on every
    /// tick — only a couple of characters around the caret are ever read.
    /// </summary>
    public void Update(ITextSource text, int caretOffset)
    {
        var (first, second) = FindPair(text, caretOffset);
        if (first == _firstOffset && second == _secondOffset)
        {
            return;
        }

        _firstOffset = first;
        _secondOffset = second;
        _textView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_firstOffset < 0 || _secondOffset < 0 || Brush is null)
        {
            return;
        }

        var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
        builder.AddSegment(textView, new TextSegment { StartOffset = _firstOffset, Length = 1 });
        builder.CloseFigure();
        builder.AddSegment(textView, new TextSegment { StartOffset = _secondOffset, Length = 1 });

        if (builder.CreateGeometry() is { } geometry)
        {
            drawingContext.DrawGeometry(Brush, null, geometry);
        }
    }

    /// <summary>
    /// The bracket adjacent to the caret and its partner, or (-1, -1) when the
    /// caret isn't at a bracket / the partner doesn't exist. The character
    /// before the caret wins over the one after it, matching how the caret
    /// visually "sits after" what was just typed.
    /// </summary>
    private static (int First, int Second) FindPair(ITextSource text, int caretOffset)
    {
        var length = text.TextLength;
        var bracketOffset = -1;
        if (caretOffset > 0 && caretOffset <= length && IsBracket(text.GetCharAt(caretOffset - 1)))
        {
            bracketOffset = caretOffset - 1;
        }
        else if (caretOffset >= 0 && caretOffset < length && IsBracket(text.GetCharAt(caretOffset)))
        {
            bracketOffset = caretOffset;
        }

        if (bracketOffset < 0)
        {
            return (-1, -1);
        }

        var c = text.GetCharAt(bracketOffset);
        var openIndex = Open.IndexOf(c);
        var forward = openIndex >= 0;
        var (open, close) = forward
            ? (c, Close[openIndex])
            : (Open[Close.IndexOf(c)], c);

        var depth = 0;
        var step = forward ? 1 : -1;
        for (var i = bracketOffset; i >= 0 && i < length; i += step)
        {
            var ch = text.GetCharAt(i);
            if (ch == open)
            {
                depth += step;
            }
            else if (ch == close)
            {
                depth -= step;
            }

            if (depth == 0)
            {
                return (Math.Min(bracketOffset, i), Math.Max(bracketOffset, i));
            }
        }

        return (-1, -1);
    }

    private static bool IsBracket(char c) => Open.Contains(c) || Close.Contains(c);
}
