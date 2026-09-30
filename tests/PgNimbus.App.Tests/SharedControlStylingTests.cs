using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Nimbus.Ui.Controls;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The shared control layer of DESIGN.md rule 20: the focus ring, the two faces of a
/// selected row, the switch, the flat buttons' hover, the menu highlight and the
/// results grid's zebra.
///
/// Every assertion reads the template part or adorner that paints, not a class or a
/// style: in this layer a rule that is present and loses (to a template-priority value,
/// a later file, a Fluent ControlTheme) looks exactly like one that works. That is not
/// hypothetical here: the first cut of the switch restyled Fluent's template parts and
/// changed nothing, because their sizes are set at Template priority, and the menu
/// radius on the highlight border lost to Fluent's template binding.
/// </summary>
public class SharedControlStylingTests
{
    [Test]
    public async Task A_button_focused_from_the_keyboard_gets_a_rounded_accent_ring_outside_it()
    {
        await Ui.Run(async () =>
        {
            var button = new Button { Content = "Run", Classes = { "soft" } };
            var window = Host(button);

            button.Focus(NavigationMethod.Tab);
            Ui.Settle();

            var ring = Adorners(button).OfType<FocusRing>().Single();
            await Assert.That(ColorOf(ring.BorderBrush)).IsEqualTo(Brush(window, "AppFocusRingBrush").Color);
            await Assert.That(ring.Margin).IsEqualTo(new Thickness(-ring.Outset));
            await Assert.That(ring.CornerRadius.TopLeft).IsEqualTo(button.CornerRadius.TopLeft + ring.Outset);
            // Outside the control's edge, and not clipped back to it.
            await Assert.That(ring.Bounds.Width).IsGreaterThan(button.Bounds.Width);
            await Assert.That(AdornerLayer.GetIsClipEnabled(ring)).IsFalse();

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_list_row_or_tree_row_draws_no_ring_of_its_own()
    {
        await Ui.Run(async () =>
        {
            var list = new ListBox { ItemsSource = new[] { "a", "b" } };
            var tree = new TreeView { ItemsSource = new[] { "x", "y" } };
            var window = Host(list, tree);

            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
            row.Focus(NavigationMethod.Tab);
            Ui.Settle();
            await Assert.That(Adorners(row).OfType<FocusRing>().Any()).IsFalse();

            var node = tree.GetVisualDescendants().OfType<TreeViewItem>().First();
            node.Focus(NavigationMethod.Tab);
            Ui.Settle();
            await Assert.That(Adorners(node).OfType<FocusRing>().Any()).IsFalse();

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_selected_row_is_accent_while_its_list_has_focus_and_grey_when_it_does_not()
    {
        await Ui.Run(async () =>
        {
            var list = new ListBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 };
            var other = new Button { Content = "elsewhere" };
            var window = Host(list, other);
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();

            other.Focus();
            Ui.Settle();
            await Assert.That(ColorOf(Part(row).Background)).IsEqualTo(Brush(window, "AppSelectionInactiveBrush").Color);

            row.Focus();
            Ui.Settle();
            await Assert.That(ColorOf(Part(row).Background)).IsEqualTo(Brush(window, "AppAccentBrush").Color);
            await Assert.That(ColorOf(Part(row).Foreground)).IsEqualTo(Colors.White);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_strip_keeps_the_light_wash_and_an_emphasized_list_is_accent_without_focus()
    {
        await Ui.Run(async () =>
        {
            var strip = new ListBox { ItemsSource = new[] { "Query 1", "Query 2" }, SelectedIndex = 0, Classes = { "strip" } };
            var palette = new ListBox { ItemsSource = new[] { "Run", "Explain" }, SelectedIndex = 0, Classes = { "emphasized" } };
            var window = Host(strip, palette);

            var tab = strip.GetVisualDescendants().OfType<ListBoxItem>().First();
            tab.Focus();
            Ui.Settle();
            await Assert.That(ColorOf(Part(tab).Background)).IsEqualTo(Brush(window, "AppSelectionBrush").Color);

            var entry = palette.GetVisualDescendants().OfType<ListBoxItem>().First();
            await Assert.That(palette.IsKeyboardFocusWithin).IsFalse();
            await Assert.That(ColorOf(Part(entry).Background)).IsEqualTo(Brush(window, "AppAccentBrush").Color);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_selected_tree_node_is_accent_with_white_text_while_the_tree_has_focus()
    {
        await Ui.Run(async () =>
        {
            var tree = new TreeView { ItemsSource = new[] { "public", "analytics" } };
            var other = new Button { Content = "elsewhere" };
            var window = Host(tree, other);
            var node = tree.GetVisualDescendants().OfType<TreeViewItem>().First();
            tree.SelectedItem = "public";

            other.Focus();
            Ui.Settle();
            await Assert.That(ColorOf(LayoutRoot(node).Background)).IsEqualTo(Brush(window, "AppSelectionInactiveBrush").Color);

            node.Focus();
            Ui.Settle();
            await Assert.That(ColorOf(LayoutRoot(node).Background)).IsEqualTo(Brush(window, "AppAccentBrush").Color);
            await Assert.That(ColorOf(HeaderPresenter(node).Foreground)).IsEqualTo(Colors.White);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_switch_is_a_32_by_18_track_with_its_knob_2px_inside_either_end()
    {
        await Ui.Run(async () =>
        {
            var off = new ToggleSwitch { IsChecked = false, OnContent = "", OffContent = "" };
            var on = new ToggleSwitch { IsChecked = true, OnContent = "", OffContent = "" };
            var window = Host(off, on);

            foreach (var toggle in new[] { off, on })
            {
                var track = toggle.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OuterBorder");
                await Assert.That(track.Bounds.Size).IsEqualTo(new Size(32, 18));
                await Assert.That(track.BorderThickness).IsEqualTo(default(Thickness));

                var knob = toggle.GetVisualDescendants().OfType<Ellipse>()
                    .Single(e => e.Name == (toggle.IsChecked == true ? "SwitchKnobOn" : "SwitchKnobOff"));
                var left = knob.TranslatePoint(default, track)!.Value.X;
                var expected = toggle.IsChecked == true ? track.Bounds.Width - 2 - knob.Bounds.Width : 2;
                await Assert.That(left).IsEqualTo(expected);
                await Assert.That(ColorOf(knob.Fill)).IsEqualTo(Colors.White);
            }

            var onTrack = on.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SwitchKnobBounds");
            await Assert.That(ColorOf(onTrack.Background)).IsEqualTo(Brush(window, "AppAccentBrush").Color);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_flat_icon_button_shows_the_toolbar_wash_under_the_pointer_and_a_checked_one_keeps_its_own()
    {
        await Ui.Run(async () =>
        {
            var chip = new Button { Content = "?", Classes = { "chip" } };
            var toolbar = new Button { Content = "Explain", Classes = { "toolbar" } };
            var on = new ToggleButton { Content = "Wrap", Classes = { "chip" }, IsChecked = true };
            var window = Host(chip, toolbar, on);

            foreach (var button in new Control[] { chip, toolbar, on })
            {
                ((IPseudoClasses)button.Classes).Add(":pointerover");
            }

            Ui.Settle();

            var hover = Brush(window, "AppToolbarHoverBrush").Color;
            await Assert.That(ColorOf(Part(chip).Background)).IsEqualTo(hover);
            await Assert.That(ColorOf(Part(toolbar).Background)).IsEqualTo(hover);
            await Assert.That(ColorOf(Part(on).Background)).IsEqualTo(Brush(window, "AppSelectionBrush").Color);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_highlighted_menu_item_is_a_rounded_accent_pill_with_white_text()
    {
        await Ui.Run(async () =>
        {
            var copy = new MenuItem { Header = "Copy" };
            var menu = new MenuFlyoutPresenter { ItemsSource = new object[] { copy, new MenuItem { Header = "Paste" } } };
            var window = Host(menu);

            ((IPseudoClasses)copy.Classes).Add(":selected");
            Ui.Settle();

            var root = copy.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
            await Assert.That(ColorOf(root.Background)).IsEqualTo(Brush(window, "AppAccentBrush").Color);
            await Assert.That(root.CornerRadius.TopLeft).IsEqualTo(4d);
            var header = copy.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_HeaderPresenter");
            await Assert.That(ColorOf(header.Foreground)).IsEqualTo(Colors.White);

            window.Close();
            Ui.Settle();
        });
    }

    /// <summary>
    /// The DataGrid has no alternating-row support and recycles rows, so the
    /// results panel sets the class itself; a row removed mid-list shifts every
    /// index after it without the grid loading those rows again.
    /// </summary>
    [Test]
    public async Task The_results_grid_stripes_every_other_row_and_restripes_after_a_row_is_removed()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "ResultsGrid");
            await Assert.That(grid.Classes.Contains("zebra")).IsTrue();
            await AssertStriped(grid, Brush(window, "AppAltRowBrush").Color);

            vm.ActiveTab.Rows.RemoveAt(0);
            Ui.Settle(passes: 4);
            await AssertStriped(grid, Brush(window, "AppAltRowBrush").Color);

            window.Close();
            Ui.Settle();
        });
    }

    private static async Task AssertStriped(DataGrid grid, Color alt)
    {
        var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible).ToList();
        await Assert.That(rows.Count).IsGreaterThan(2);

        foreach (var row in rows)
        {
            var odd = row.Index % 2 == 1;
            await Assert.That(row.Classes.Contains("odd")).IsEqualTo(odd);
            if (odd)
            {
                await Assert.That(ColorOf(row.Background)).IsEqualTo(alt);
            }
        }
    }

    private static Window Host(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        panel.Children.AddRange(children);
        var window = new Window { Width = 500, Height = 500, Content = panel };
        Ui.Show(window);
        return window;
    }

    private static IEnumerable<Control> Adorners(Visual adorned) =>
        AdornerLayer.GetAdornerLayer(adorned)?.Children ?? [];

    private static ContentPresenter Part(Visual control) =>
        control.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");

    private static Border LayoutRoot(TreeViewItem node) =>
        node.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");

    private static ContentPresenter HeaderPresenter(TreeViewItem node) =>
        node.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_HeaderPresenter");

    private static ISolidColorBrush Brush(Control anchor, string key) =>
        anchor.TryFindResource(key, anchor.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush
            : throw new InvalidOperationException($"{key} is not a solid brush in the shared tokens.");

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;
}
