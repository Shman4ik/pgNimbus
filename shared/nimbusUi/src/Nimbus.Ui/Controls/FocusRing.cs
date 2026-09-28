using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Nimbus.Ui.Controls;

/// <summary>
/// The keyboard focus ring both apps draw (DESIGN.md rule 20): a semi-transparent
/// brand-accent outline just outside the focused control, rounded to that control's own
/// corners.
/// <para>
/// It is the adorner template every <see cref="AdornerLayer"/> hands out by default
/// (<c>AdornerLayer.DefaultFocusAdorner</c>, set in <c>Theme/Controls.axaml</c>), which
/// is what Avalonia shows for any control that does not name a
/// <see cref="Control.FocusAdorner"/> of its own. Fluent's own default was a 2px black
/// rectangle (white in dark) with a 1px inner rule, square whatever it surrounded — a
/// tree row, a round chip, a pill button all got the same hard box, which is the one
/// piece of Windows-95 chrome left in either app.
/// </para>
/// <para>
/// The corners are read from the adorned element when the ring is attached. An adorner
/// is a sibling of the control in the adorner layer, not a child, so it cannot inherit
/// or template-bind the radius; <see cref="AdornerLayer.AdornedElementProperty"/> is the
/// one link back, and a control with no corners of its own (a template's focus target
/// is often a bare <see cref="Panel"/>) gets <see cref="FallbackRadius"/> rather than a
/// square ring.
/// </para>
/// </summary>
public class FocusRing : Border
{
    /// <summary>How far outside the control's edge the ring sits, in DIPs.</summary>
    public static readonly StyledProperty<double> OutsetProperty =
        AvaloniaProperty.Register<FocusRing, double>(nameof(Outset), 2);

    /// <summary>The radius used around a control that has no corner radius of its own.</summary>
    public const double FallbackRadius = 4;

    public FocusRing()
    {
        IsHitTestVisible = false;

        // The adorner layer clips an adorner to its element's bounds by default,
        // which removes a ring drawn outside them entirely (it rendered as one
        // stray corner pixel).
        AdornerLayer.SetIsClipEnabled(this, false);
    }

    /// <inheritdoc cref="OutsetProperty"/>
    public double Outset
    {
        get => GetValue(OutsetProperty);
        set => SetValue(OutsetProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AdornerLayer.AdornedElementProperty || change.Property == OutsetProperty)
        {
            Sync();
        }
    }

    private void Sync()
    {
        var outset = Outset;
        Margin = new Thickness(-outset);
        CornerRadius = Grow(RadiusOf(AdornerLayer.GetAdornedElement(this)), outset);
    }

    /// <summary>
    /// The corner radius of the element the ring surrounds: a templated control's or a
    /// border's own, else <see cref="FallbackRadius"/>.
    /// </summary>
    public static CornerRadius RadiusOf(Visual? adorned)
    {
        var radius = adorned switch
        {
            TemplatedControl templated => templated.CornerRadius,
            Border border => border.CornerRadius,
            _ => default,
        };

        return radius == default ? new CornerRadius(FallbackRadius) : radius;
    }

    // A ring drawn `outset` outside a rounded edge follows it at radius + outset;
    // anything less pinches at the corners.
    private static CornerRadius Grow(CornerRadius r, double by) =>
        new(r.TopLeft + by, r.TopRight + by, r.BottomRight + by, r.BottomLeft + by);
}
