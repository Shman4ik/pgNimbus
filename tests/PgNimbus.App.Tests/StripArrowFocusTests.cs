using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Nimbus.Ui.Controls;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Left and Right along a strip keep the focus ring on the item they reach (nimbusUi's
/// <c>ArrowKeyFocus</c>, kubeNimbus#297). A <see cref="SelectingItemsControl"/>'s own arrow
/// handling moved the selection and focused the next item with no navigation method, so
/// <c>:focus-visible</c> went off and the ring with it, and a strip's selected segment looks
/// the same focused or not. Remove the <c>ArrowKeyFocus.IsEnabled</c> setter from the
/// strip's style and these fail.
/// </summary>
public class StripArrowFocusTests
{
    [Test]
    public async Task The_sidebar_capsule_keeps_the_ring_on_the_arrow_keys()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Results();
            Ui.Show(window);

            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "SidebarTabs");
            await AssertArrowsKeepTheRing(window, tabs);

            window.Close();
        });
    }

    [Test]
    public async Task A_segmented_tab_control_keeps_the_ring_on_the_arrow_keys()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Security();
            Ui.Show(window);

            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("segmented"));
            await AssertArrowsKeepTheRing(window, tabs);

            window.Close();
        });
    }

    [Test]
    public async Task The_editor_tab_strip_keeps_the_ring_on_the_arrow_keys()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Results();
            Ui.Show(window);

            var strip = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TabsList");
            if (strip.ItemCount < 2)
            {
                ((ViewModels.MainViewModel)window.DataContext!).AddTabCommand.Execute(null);
                Ui.Settle();
            }

            await AssertArrowsKeepTheRing(window, strip);

            window.Close();
        });
    }

    private static async Task AssertArrowsKeepTheRing(Window window, SelectingItemsControl strip)
    {
        await Assert.That(strip.ItemCount).IsGreaterThanOrEqualTo(2);

        var start = strip.SelectedIndex;
        var (there, home) = start + 1 < strip.ItemCount ? (Key.Right, Key.Left) : (Key.Left, Key.Right);
        var next = there == Key.Right ? start + 1 : start - 1;

        strip.ContainerFromIndex(start)!.Focus(NavigationMethod.Tab);
        Ui.Settle();

        Ui.Press(window, there);
        await Assert.That(strip.SelectedIndex).IsEqualTo(next);
        await Assert.That(FocusedIndex(window, strip)).IsEqualTo(next);
        await Assert.That(HasRing(window)).IsTrue();

        Ui.Press(window, home);
        await Assert.That(strip.SelectedIndex).IsEqualTo(start);
        await Assert.That(FocusedIndex(window, strip)).IsEqualTo(start);
        await Assert.That(HasRing(window)).IsTrue();
    }

    private static int FocusedIndex(Window window, SelectingItemsControl strip) =>
        window.FocusManager?.GetFocusedElement() is Control focused ? strip.IndexFromContainer(focused) : -1;

    /// <summary>The shared ring is in the adorner layer over whatever has focus.</summary>
    private static bool HasRing(Window window) =>
        window.FocusManager?.GetFocusedElement() is Visual focused
        && AdornerLayer.GetAdornerLayer(focused) is { } layer
        && layer.Children.Any(c => c is FocusRing && AdornerLayer.GetAdornedElement(c) == focused);
}
