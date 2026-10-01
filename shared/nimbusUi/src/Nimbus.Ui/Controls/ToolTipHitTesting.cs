using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Nimbus.Ui.Controls;

/// <summary>
/// Makes every element that carries a tooltip answer the pointer (DESIGN.md rule 21).
/// <para>
/// A tooltip opens on the element the pointer is over, and Avalonia's hit test finds
/// an element only where it draws something. A <see cref="TextBlock"/> or a
/// <see cref="Panel"/> with no background draws nothing of its own (glyphs are not
/// hit-testable), so the pointer over its text reaches whatever lies behind it — the
/// list row, the card, the window — and the tooltip never opens. pgNimbus measured it
/// headlessly: none of 4,536 points over its status line reached the text block, and
/// a walk of every window found history rows, profile endpoints, column headers and
/// cut query text all carrying tooltips nobody could open.
/// </para>
/// <para>
/// The answer is the one Avalonia documents, a transparent background, applied by a
/// class handler rather than written at each site: when an element gets a tooltip and
/// has no background, it is given <see cref="Brushes.Transparent"/> as a
/// <em>current</em> value, so any background a style or the markup sets still wins
/// and nothing changes on screen. A background that later goes back to null (a style
/// that stops matching) is filled again. An element with <c>IsHitTestVisible</c>
/// off is left alone, and so is a disabled one: Avalonia hides tooltips on disabled
/// controls unless they opt in with <c>ToolTip.ShowOnDisabled</c>.
/// </para>
/// </summary>
public static class ToolTipHitTesting
{
    private static bool _installed;

    /// <summary>
    /// Registers the class handlers. Call once, from <c>Application.Initialize</c>,
    /// before the first window is built.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) => Fill(control));
        Panel.BackgroundProperty.Changed.AddClassHandler<Panel>((control, _) => Fill(control));
        TextBlock.BackgroundProperty.Changed.AddClassHandler<TextBlock>((control, _) => Fill(control));
        Border.BackgroundProperty.Changed.AddClassHandler<Border>((control, _) => Fill(control));
        TemplatedControl.BackgroundProperty.Changed.AddClassHandler<TemplatedControl>((control, _) => Fill(control));
        ContentPresenter.BackgroundProperty.Changed.AddClassHandler<ContentPresenter>((control, _) => Fill(control));
    }

    private static void Fill(Control control)
    {
        if (ToolTip.GetTip(control) is null || BackgroundOf(control) is not { } background
            || control.GetValue(background) is not null)
        {
            return;
        }

        control.SetCurrentValue(background, Brushes.Transparent);
    }

    private static StyledProperty<IBrush?>? BackgroundOf(Control control) => control switch
    {
        Panel => Panel.BackgroundProperty,
        TextBlock => TextBlock.BackgroundProperty,
        Border => Border.BackgroundProperty,
        TemplatedControl => TemplatedControl.BackgroundProperty,
        ContentPresenter => ContentPresenter.BackgroundProperty,
        _ => null,
    };
}
