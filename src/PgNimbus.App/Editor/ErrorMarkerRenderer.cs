using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using PgNimbus.Core.Query;

namespace PgNimbus.App;

/// <summary>
/// Draws a wavy underline under the place a failed run's server error points at
/// (<see cref="ViewModels.QueryViewModel.ErrorMarker"/>). Owned by
/// <c>QueryEditorPanel</c>, which hands it the active tab's span; it reads nothing
/// from the document but the visual lines already on screen, so it costs nothing
/// per keystroke.
/// </summary>
public sealed class ErrorMarkerRenderer : IBackgroundRenderer
{
    private const double WaveLength = 4;
    private const double WaveHeight = 2;

    private readonly TextView _textView;
    private SqlErrorSpan? _span;

    public ErrorMarkerRenderer(TextView textView)
    {
        _textView = textView;
        textView.BackgroundRenderers.Add(this);
    }

    /// <summary>Theme-resolved underline brush (<c>AppDangerBrush</c>); set by the panel.</summary>
    public IBrush? Brush { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>The marked span in document offsets, or null for none.</summary>
    public SqlErrorSpan? Span
    {
        get => _span;
        set
        {
            if (_span == value)
            {
                return;
            }

            _span = value;
            _textView.InvalidateLayer(Layer);
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_span is not { } span || Brush is null || textView.Document is not { } document
            || span.Length <= 0 || span.End > document.TextLength || !textView.VisualLinesValid)
        {
            return;
        }

        var pen = new Pen(Brush, 1.2);
        var segment = new TextSegment { StartOffset = span.Start, Length = span.Length };
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
        {
            DrawWave(drawingContext, pen, rect.Left, rect.Right, rect.Bottom - WaveHeight);
        }
    }

    private static void DrawWave(DrawingContext context, IPen pen, double left, double right, double top)
    {
        if (right - left < 1)
        {
            right = left + WaveLength;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var x = left;
            var up = false;
            g.BeginFigure(new Point(x, top + WaveHeight), false);
            while (x < right)
            {
                x = Math.Min(x + (WaveLength / 2), right);
                g.LineTo(new Point(x, up ? top + WaveHeight : top));
                up = !up;
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
