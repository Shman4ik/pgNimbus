using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.VisualTree;

namespace PgNimbus.Screenshot;

/// <summary>
/// Pins the one piece of a rendered frame that follows the wall clock.
/// Avalonia's <c>CalendarDatePicker</c> draws today's day number on its
/// calendar button: the template binds it to <c>DateTime.Today</c>. Every
/// scenario with a date picker (the filter editor, row details, the role
/// dialog) therefore drew a different frame each day. On 2026-10-01 the
/// filter editor's two digits came to 0.10–0.12 % of its pixels against the
/// baselines rendered on the 28th, past the 0.1 % tolerance, and every open
/// PR's visual-regression check failed at once.
/// <para>
/// The number cannot be pinned by a style: neither a plain style nor one with
/// an activator outranks the template's binding. So it is set from code at
/// <see cref="BindingPriority.Animation"/>, the highest priority there is,
/// on each frame before it is captured.
/// </para>
/// </summary>
public static class FixedToday
{
    /// <summary>
    /// The day every frame shows. Any constant would do; this one is the day the
    /// committed baselines were rendered, so pinning it changed no baseline.
    /// </summary>
    public const int Day = 28;

    /// <summary>
    /// Writes <see cref="Day"/> over today's day number on every date picker
    /// under <paramref name="root"/>. Call it after layout, when the pickers'
    /// templates have been applied.
    /// </summary>
    /// <returns>How many day numbers it pinned.</returns>
    public static int Pin(Visual root)
    {
        var today = DateTime.Today.Day.ToString(CultureInfo.InvariantCulture);
        var pinned = 0;
        foreach (var picker in root.GetVisualDescendants().OfType<CalendarDatePicker>())
        {
            var button = picker.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PART_Button");
            if (button is null)
            {
                continue;
            }

            foreach (var text in button.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == today))
            {
                text.SetValue(TextBlock.TextProperty, Day.ToString(CultureInfo.InvariantCulture), BindingPriority.Animation);
                pinned++;
            }
        }

        return pinned;
    }
}
