using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The sidebar's layout after the compact pass: the Schemas / Queries switch,
/// the schema tree's right-hand size column, and the history list's rows and
/// right-click menu (which replaced the stock Load / Clear buttons under it).
/// </summary>
public class SidebarTests
{
    /// <summary>
    /// The two tall left-nav pills became one capsule the height of the filter
    /// box, split into equal halves. A switch that grew back to a pill would take
    /// the tree's first rows again, which is what this pass was for.
    /// </summary>
    [Test]
    public async Task The_tab_switch_is_one_row_of_equal_segments()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Results();
            Ui.Show(window);

            var tabs = window.FindControl<TabControl>("SidebarTabs")!;
            var items = tabs.GetLogicalChildren().OfType<TabItem>().ToList();

            await Assert.That(tabs.Classes).Contains("capsule");
            await Assert.That(items.Count).IsEqualTo(2);
            await Assert.That(items[0].Bounds.Height).IsLessThanOrEqualTo(28);
            await Assert.That(items[0].Bounds.Y).IsEqualTo(items[1].Bounds.Y);
            await Assert.That(Math.Abs(items[0].Bounds.Width - items[1].Bounds.Width)).IsLessThan(1);

            window.Close();
        });
    }

    /// <summary>
    /// Sizes line up in a column at the tree's right edge instead of trailing
    /// each name, and the tree doesn't scroll sideways, so a long name trims
    /// rather than pushing the column off the panel.
    /// </summary>
    [Test]
    public async Task Table_sizes_line_up_at_the_right_edge()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Results();
            Ui.Show(window);

            var tree = window.GetVisualDescendants().OfType<TreeView>().First(t => t.Name == "SchemaTreeView");
            await Assert.That(ScrollViewer.GetHorizontalScrollBarVisibility(tree)).IsEqualTo(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

            var sizes = tree.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.DataContext is TableNode && t.Text is { Length: > 0 } text && text.EndsWith("MB"))
                .ToList();
            await Assert.That(sizes.Count).IsGreaterThanOrEqualTo(2);

            var rightEdges = sizes.Select(t => t.TranslatePoint(new Point(t.Bounds.Width, 0), tree)!.Value.X).ToList();
            await Assert.That(rightEdges.Max() - rightEdges.Min()).IsLessThan(1);
            await Assert.That(tree.Bounds.Width - rightEdges[0]).IsLessThan(24);

            window.Close();
        });
    }

    /// <summary>
    /// A history row is the statement on one line and a short meta line; the raw
    /// timestamp (in the OS culture, with its offset) and the query's own line
    /// breaks are gone, and the connection is named only on another one's rows.
    /// </summary>
    [Test]
    public async Task A_history_row_is_one_line_of_sql_and_a_short_meta_line()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.QueriesSidebar();
            Ui.Show(window);

            var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "HistoryList");
            var texts = list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

            await Assert.That(texts.Any(t => t.Contains('\n'))).IsFalse();
            await Assert.That(texts).Contains("SELECT o.id, c.full_name AS customer, o.status, o.total, o.paid, o.metadata, o.placed_at FROM orders AS o JOIN customers AS c ON c.id = o.customer_id WHERE o.placed_at > now() - interval '30 days' ORDER BY o.placed_at DESC;");
            await Assert.That(texts).Contains("09:41 · 18 ms · 50 rows");
            await Assert.That(texts).Contains("Mon 09:41 · 12 ms · 48 rows · prod/shop");
            await Assert.That(texts.Any(t => t.Contains("+00:00"))).IsFalse();

            window.Close();
        });
    }

    /// <summary>
    /// The pin shows on a pinned entry and stays out of the way on the others
    /// until the row is pointed at or selected.
    /// </summary>
    [Test]
    public async Task Only_a_pinned_rows_pin_shows_at_rest()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.QueriesSidebar();
            Ui.Show(window);

            var pins = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("historyPin"))
                .ToList();
            var pinned = pins.Single(b => b.DataContext is QueryHistoryEntry { Pinned: true });
            var unpinned = pins.First(b => b.DataContext is QueryHistoryEntry { Pinned: false });

            await Assert.That(pinned.Opacity).IsEqualTo(1);
            await Assert.That(unpinned.Opacity).IsEqualTo(0);

            window.Close();
        });
    }

    /// <summary>
    /// Load and Clear left the button row under the list for its right-click
    /// menu, where Pin follows the entry clicked.
    /// </summary>
    [Test]
    public async Task The_history_menu_opens_copies_pins_and_clears()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.QueriesSidebar();
            Ui.Show(window);

            var panel = window.GetVisualDescendants().OfType<Views.SavedQueriesPanel>().Single();
            await Assert.That(panel.GetVisualDescendants().OfType<Button>().Any(b => b.Content is string)).IsFalse();

            var list = panel.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "HistoryList");
            var flyout = (MenuFlyout)list.ContextFlyout!;
            var model = (SavedQueriesViewModel)panel.DataContext!;

            list.SelectedItem = model.FilteredHistory.First(e => e.Pinned);
            flyout.ShowAt(list);
            Ui.Settle();
            var headers = flyout.Items.OfType<MenuItem>().Select(i => i.Header as string ?? string.Empty).ToArray();
            flyout.Hide();
            Ui.Settle();
            await Assert.That(headers).IsEquivalentTo(new[] { "Open in New Tab", "Copy SQL", "Unpin", "Clear History" });

            list.SelectedItem = model.FilteredHistory.First(e => !e.Pinned);
            flyout.ShowAt(list);
            Ui.Settle();
            var pin = flyout.Items.OfType<MenuItem>().Single(i => i.Name == "PinHistoryMenuItem");
            await Assert.That(pin.Header).IsEqualTo("Pin");
            flyout.Hide();
            Ui.Settle();

            window.Close();
        });
    }
}
