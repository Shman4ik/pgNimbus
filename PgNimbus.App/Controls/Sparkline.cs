using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace PgNimbus.App.Controls;

/// <summary>
/// A tiny line-plus-area chart for a bounded series of samples — enough to tell
/// a spike from a steady state, which a single number never can.
///
/// Hand-rolled on purpose: the Avalonia charting packages bring reflection-based
/// binding and theming with them, which the NativeAOT publish (the shipping
/// build) can't accept. This draws straight into a <see cref="DrawingContext"/>,
/// so there is no reflection and no template to resolve.
///
/// <see cref="Values"/> is nullable per point and a <c>null</c> is a <b>gap, not
/// a zero</b>: the line breaks across it. A server that stopped answering must
/// not draw the same shape as a server that went quiet.
/// </summary>
public sealed class Sparkline : Control
{
    /// <summary>Headroom above the series peak, so the tallest point isn't flush against the top edge.</summary>
    private const double Headroom = 0.12;

    public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double?>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 1.25);

    /// <summary>
    /// The floor the vertical scale is measured against, so a series that never
    /// exceeds it stays visually flat instead of amplifying noise into a
    /// mountain range (1 backend vs 2 is not a spike).
    /// </summary>
    public static readonly StyledProperty<double> MinimumScaleProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(MinimumScale), 1);

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, StrokeThicknessProperty, MinimumScaleProperty);
    }

    public IReadOnlyList<double?>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double MinimumScale
    {
        get => GetValue(MinimumScaleProperty);
        set => SetValue(MinimumScaleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is null || values.Count == 0 || Stroke is not { } stroke)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var peak = Math.Max(MinimumScale, values.Max(v => v ?? 0)) * (1 + Headroom);
        var pen = new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        // The area under the line reads as volume without competing with the
        // line itself, so it's the same brush at a low opacity rather than a
        // second themed resource to keep in step.
        var fill = new ImmutableSolidColorBrush(
            (stroke as ISolidColorBrush)?.Color ?? Colors.Gray,
            opacity: 0.18);

        // A single point has no width to span, so treat the series as if it
        // started one step to the left rather than dividing by zero.
        var step = values.Count > 1 ? width / (values.Count - 1) : width;

        // One geometry per run of consecutive readings: a gap ends the current
        // run and the next reading starts a new one, so the line never bridges
        // a period nothing was measured.
        var run = new List<Point>();
        for (var i = 0; i <= values.Count; i++)
        {
            var value = i < values.Count ? values[i] : null;
            if (value is { } v)
            {
                var y = height - Math.Clamp(v / peak, 0, 1) * height;
                run.Add(new Point(i * step, y));
                continue;
            }

            DrawRun(context, run, pen, fill, height);
            run.Clear();
        }
    }

    private static void DrawRun(DrawingContext context, List<Point> run, Pen pen, IBrush fill, double height)
    {
        if (run.Count == 0)
        {
            return;
        }

        // A lone reading between two gaps has no line to draw — a dot keeps it
        // visible rather than silently dropping the only thing measured.
        if (run.Count == 1)
        {
            context.DrawEllipse(pen.Brush, null, run[0], pen.Thickness, pen.Thickness);
            return;
        }

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(run[0].X, height), isFilled: true);
            foreach (var point in run)
            {
                ctx.LineTo(point);
            }

            ctx.LineTo(new Point(run[^1].X, height));
            ctx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(run[0], isFilled: false);
            for (var i = 1; i < run.Count; i++)
            {
                ctx.LineTo(run[i]);
            }

            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, line);
    }
}
