using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The Settings page's "Record query history" switch (security audit 2026-09,
/// finding 8: history kept every statement and there was no way to stop it).
/// Off, a run files nothing, in the list or on disk, and the sidebar says why
/// instead of showing a list that silently stopped growing.
/// </summary>
public class QueryHistoryPreferenceTests
{
    [Test]
    public async Task Turning_history_off_in_Settings_stops_recording_and_the_sidebar_says_so()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.QueriesSidebar();
            var vm = (MainViewModel)window.DataContext!;
            Ui.Show(window);

            var history = vm.SavedQueries;
            var panel = window.GetVisualDescendants().OfType<Views.SavedQueriesPanel>().Single();
            var offHint = panel.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "HistoryOffHint");
            await Assert.That(history.RecordHistory).IsTrue();
            await Assert.That(offHint.IsVisible).IsFalse();

            vm.IsPreferencesOpen = true;
            Ui.Settle();
            var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "RecordQueryHistorySwitch");
            toggle.BringIntoView();
            Ui.Settle();
            await Assert.That(toggle.IsChecked == true).IsTrue();

            Click(window, toggle);

            await Assert.That(toggle.IsChecked == false).IsTrue();
            await Assert.That(history.RecordHistory).IsFalse();

            vm.IsPreferencesOpen = false;
            Ui.Settle();
            await Assert.That(offHint.IsVisible).IsTrue();

            var before = history.History.Count;
            history.RecordExecution(new QueryHistoryEntry("SELECT 'not recorded';", DateTimeOffset.UtcNow, 1, "1 row"));
            await Assert.That(history.History.Count).IsEqualTo(before);
            var file = history.HistoryStore.FilePath;
            await Assert.That(!File.Exists(file) || !File.ReadAllText(file).Contains("not recorded")).IsTrue();

            // And back on: the next run is filed and the line goes away.
            // The page is built afresh each time it opens.
            vm.IsPreferencesOpen = true;
            Ui.Settle();
            toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "RecordQueryHistorySwitch");
            toggle.BringIntoView();
            Ui.Settle();
            await Assert.That(toggle.IsChecked == false).IsTrue();
            Click(window, toggle);
            vm.IsPreferencesOpen = false;
            Ui.Settle();

            await Assert.That(history.RecordHistory).IsTrue();
            await Assert.That(offHint.IsVisible).IsFalse();
            history.RecordExecution(new QueryHistoryEntry("SELECT 'recorded';", DateTimeOffset.UtcNow, 1, "1 row"));
            await Assert.That(history.History[0].Sql).IsEqualTo("SELECT 'recorded';");

            window.Close();
        });
    }

    [Test]
    public async Task An_empty_history_with_recording_off_explains_itself_instead_of_promising_entries()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.QueriesSidebar();
            var vm = (MainViewModel)window.DataContext!;
            Ui.Show(window);

            var history = vm.SavedQueries;
            history.History.Clear();
            history.RecordHistory = false;
            Ui.Settle();

            var texts = window.GetVisualDescendants().OfType<Views.SavedQueriesPanel>().Single()
                .GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            await Assert.That(texts).Contains("History is off, so queries you run are not recorded. Turn it on in Settings.");
            await Assert.That(texts).DoesNotContain("Queries you run will appear here");

            window.Close();
        });
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Ui.Settle();
    }
}
