using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The Settings page is four tabs (2026-10). It keeps one height whichever tab
/// shows, so the strip does not move under the pointer, and it opens on the tab
/// it was left on.
/// </summary>
public class SettingsTabsTests
{
    [Test]
    public async Task Switching_tabs_keeps_the_page_height_and_reopening_keeps_the_tab()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Preferences();
            var vm = (MainViewModel)window.DataContext!;
            Ui.Show(window);

            var page = window.GetVisualDescendants().OfType<PreferencesView>().Single();
            var tabs = page.GetVisualDescendants().OfType<TabControl>().Single();
            await Assert.That(tabs.ItemCount).IsEqualTo(4);
            await Assert.That(tabs.SelectedIndex).IsEqualTo(0);

            var heights = new List<double>();
            for (var i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                Ui.Settle();
                heights.Add(page.Bounds.Height);
            }

            await Assert.That(heights.Distinct().Count()).IsEqualTo(1);
            await Assert.That(heights[0]).IsEqualTo(PreferencesView.PageHeight);

            tabs.SelectedIndex = 2;
            Ui.Settle();
            vm.IsPreferencesOpen = false;
            Ui.Settle();
            vm.IsPreferencesOpen = true;
            Ui.Settle();

            page = window.GetVisualDescendants().OfType<PreferencesView>().Single();
            tabs = page.GetVisualDescendants().OfType<TabControl>().Single();
            await Assert.That(vm.Preferences!.SelectedTab).IsEqualTo(2);
            await Assert.That(tabs.SelectedIndex).IsEqualTo(2);

            window.Close();
        });
    }
}
