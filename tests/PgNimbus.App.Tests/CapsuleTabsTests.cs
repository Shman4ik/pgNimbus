using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The capsule tab strip (shared/nimbusUi's TabControl.capsule) sits its selected
/// segment in the middle of the strip: the same gap above, below and at the ends.
/// Fluent's TabControl theme gives the part named PART_ItemsPresenter a bottom
/// margin (TabControlTopPlacementItemMargin), and the capsule template keeps that
/// name, so the pill used to sit 2px from the top and 4px from the bottom.
/// </summary>
public class CapsuleTabsTests
{
    [Test]
    public async Task The_selected_segment_is_centred_in_the_strip()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Results();
            Ui.Show(window);

            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "SidebarTabs");
            var strip = tabs.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_StripBorder");
            var item = tabs.GetVisualDescendants().OfType<TabItem>().First(t => t.IsSelected);

            var origin = item.TranslatePoint(new Point(0, 0), strip)!.Value;
            var top = origin.Y;
            var left = origin.X;
            var bottom = strip.Bounds.Height - (origin.Y + item.Bounds.Height);

            await Assert.That(bottom).IsEqualTo(top);
            await Assert.That(left).IsEqualTo(top);

            window.Close();
        });
    }
}
