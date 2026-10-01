using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A date picker draws today's day number on its calendar button, so every
/// screenshot scenario with one rendered a different frame each day, and on
/// 2026-10-01 the filter editor's moved past the visual-regression tolerance
/// for every open PR at once. The harness pins it (<see cref="FixedToday"/>).
/// </summary>
public class FixedTodayTests
{
    [Test]
    public async Task Every_date_picker_in_a_scenario_shows_the_pinned_day()
    {
        await Ui.Run(async () =>
        {
            foreach (var window in new[] { Scenarios.FilterEditor() })
            {
                Ui.Show(window);
                var pinned = FixedToday.Pin(window);
                Ui.Settle();

                var days = window.GetVisualDescendants().OfType<CalendarDatePicker>()
                    .SelectMany(p => p.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "PART_Button"))
                    .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
                    .Select(t => t.Text)
                    .ToList();

                // If Avalonia stops drawing the day there, this says so instead
                // of the pin silently doing nothing.
                await Assert.That(pinned).IsGreaterThan(0);
                await Assert.That(days).Contains(FixedToday.Day.ToString());
                await Assert.That(days).DoesNotContain(DateTime.Today.Day == FixedToday.Day ? "never" : DateTime.Today.Day.ToString());
                window.Close();
            }
        });
    }
}
