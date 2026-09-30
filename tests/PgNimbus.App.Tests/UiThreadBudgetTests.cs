using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Notifications;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The UI-thread audit of 2026-09 (docs/dev/design/ui-thread-audit.md), held as
/// budgets: how many rows a view realizes for a big list, and how many change
/// notifications one operation raises. Both are counts, not timings, so they are
/// as stable on a CI runner as on a laptop, and each one is the number that went
/// from "every item" to "a screenful" or from "one per item" to "one". The
/// timings themselves are tools/UiBench's, charted per release.
/// </summary>
public class UiThreadBudgetTests
{
    // A 1440 × 900 window shows about thirty tree rows; the tree keeps a viewport
    // realized past each edge (CacheLength), so a screenful is under a hundred.
    // Anything near the item count means virtualization is gone.
    private const int ScreenfulBudget = 250;

    private static SchemaNode BigSchema(int tables)
    {
        var service = new SchemaService(Fixtures.DataSource);
        var schema = new SchemaNode(service, "big", () => false, () => false);
        schema.SeedChildren(Enumerable.Range(0, tables)
            .Select(i => (SchemaTreeNode)new TableNode(service, "big", $"table_{i:D5}", RelationKind.Table)));
        return schema;
    }

    private static int RealizedRowsOf(Window window, SchemaNode schema) =>
        window.GetVisualDescendants().OfType<TreeViewItem>()
            .Count(item => item.DataContext is TableNode table && table.Schema == schema.Name);

    private static int CountChanges(INotifyCollectionChanged collection, Action action)
    {
        var count = 0;
        NotifyCollectionChangedEventHandler handler = (_, _) => count++;
        collection.CollectionChanged += handler;
        try
        {
            action();
        }
        finally
        {
            collection.CollectionChanged -= handler;
        }

        return count;
    }

    [Test]
    public async Task A_schema_of_five_thousand_tables_expands_a_screenful_and_the_arrows_walk_past_it()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var schema = BigSchema(5_000);
            vm.SchemaTree.Schemas.Insert(0, schema);
            Ui.Settle();

            schema.IsExpanded = true;
            Ui.Settle();

            var realized = RealizedRowsOf(window, schema);
            await Assert.That(realized).IsGreaterThan(10);
            await Assert.That(realized).IsLessThan(ScreenfulBudget);

            // TreeView moves focus only to a row that exists; the cache past the
            // viewport is what lets Down go beyond the rows first realized.
            var tree = window.GetVisualDescendants().OfType<TreeView>().First(t => t.Name == "SchemaTreeView");
            var first = window.GetVisualDescendants().OfType<TreeViewItem>().First(i => i.DataContext == schema.Children[0]);
            tree.SelectedItem = schema.Children[0];
            first.Focus();
            Ui.Settle();
            for (var i = 0; i < 120; i++)
            {
                Ui.Press(window, Key.Down);
            }

            await Assert.That((tree.SelectedItem as SchemaTreeNode)?.Name).IsEqualTo("table_00120");
            await Assert.That(RealizedRowsOf(window, schema)).IsLessThan(ScreenfulBudget);

            window.Close();
        });
    }

    [Test]
    public async Task A_filter_that_hides_most_tables_leaves_them_unrealized()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var schema = BigSchema(5_000);
            vm.SchemaTree.Schemas.Insert(0, schema);
            schema.IsExpanded = true;
            Ui.Settle();

            // A hidden row left in the list is still realized by a virtualizing
            // panel (to learn it takes no space): 5,000 of them for this filter.
            vm.SchemaTree.FilterText = "table_04999";
            Ui.Settle();

            await Assert.That(schema.ShownChildren.Count).IsEqualTo(1);
            await Assert.That(RealizedRowsOf(window, schema)).IsEqualTo(1);

            vm.SchemaTree.FilterText = string.Empty;
            Ui.Settle();

            await Assert.That(schema.ShownChildren.Count).IsEqualTo(5_000);
            await Assert.That(RealizedRowsOf(window, schema)).IsLessThan(ScreenfulBudget);

            window.Close();
        });
    }

    [Test]
    public async Task A_schema_s_tables_arrive_in_one_change_and_are_filtered_once()
    {
        await Ui.Run(async () =>
        {
            var service = new SchemaService(Fixtures.DataSource);
            var tree = new SchemaTreeViewModel(service);
            var schema = new SchemaNode(service, "big", () => false, () => false);
            tree.Schemas.Add(schema);
            tree.FilterText = "table_00";

            var children = 0;
            var shown = CountChanges(schema.ShownChildren, () =>
                children = CountChanges(schema.Children, () =>
                    schema.SeedChildren(Enumerable.Range(0, 5_000)
                        .Select(i => (SchemaTreeNode)new TableNode(service, "big", $"table_{i:D5}", RelationKind.Table)))));

            // Loading used to be a Clear and an Add per table, and each Add re-vetted
            // the whole schema against the filter: quadratic.
            await Assert.That(children).IsEqualTo(1);
            await Assert.That(shown).IsLessThanOrEqualTo(2);
            await Assert.That(schema.ShownChildren.Count).IsEqualTo(1_000); // table_00000 … table_00999
        });
    }

    [Test]
    public async Task A_script_of_five_thousand_statements_realizes_a_screenful_of_sections()
    {
        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel();
            var tab = vm.ActiveTab;
            tab.ResultSections.AddRange([.. Enumerable.Range(1, 5_000).Select(i => ScriptResultViewModel.From(
                i, $"INSERT INTO t VALUES ({i})", new CommandResult { Elapsed = TimeSpan.FromMilliseconds(1), RowsAffected = 1, CommandTag = "INSERT 0 1" }))]);
            tab.SelectedSection = tab.ResultSections[0];
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            Ui.Show(window);

            var chips = window.GetVisualDescendants().OfType<ListBoxItem>().Count(item => item.DataContext is ScriptResultViewModel);
            await Assert.That(chips).IsGreaterThan(3);
            await Assert.That(chips).IsLessThan(ScreenfulBudget);

            window.Close();
        });
    }

    [Test]
    public async Task The_palette_rebuilds_with_one_change_per_keystroke_and_narrows_to_the_same_result()
    {
        await Ui.Run(async () =>
        {
            var palette = new CommandPaletteViewModel();
            var random = new Random(7);
            var items = Enumerable.Range(0, 20_000)
                .Select(i => new PaletteItem($"schema_{random.Next(40)}.table_{i}_{(char)('a' + random.Next(26))}", "Table", "▦", () => Task.CompletedTask))
                .ToList();
            palette.Open(items);

            foreach (var query in new[] { "t", "ta", "tab", "tab1", "tab12", "ta", "sch", "sch_1" })
            {
                var changes = CountChanges(palette.Results, () => palette.SearchText = query);
                await Assert.That(changes).IsEqualTo(1);

                // Narrowing (scoring only the previous keystroke's matches) must
                // give what scoring every candidate gives.
                var expected = items
                    .Select((item, index) => (item, index, score: Core.Text.FuzzyMatcher.Score($"{item.Title} {item.Category}", query)))
                    .Where(x => x.score is not null)
                    .OrderByDescending(x => x.score!.Value).ThenBy(x => x.index)
                    .Select(x => x.item.Title)
                    .ToList();
                await Assert.That(palette.Results.Select(r => r.Title).ToList()).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    public async Task History_changes_are_one_change_each_and_the_list_keeps_its_cap()
    {
        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel();
            var history = vm.SavedQueries;

            for (var i = 0; i < QueryHistoryStore.MaxEntries + 20; i++)
            {
                var changes = CountChanges(history.FilteredHistory,
                    () => history.RecordExecution(new QueryHistoryEntry($"SELECT {i};", DateTimeOffset.UtcNow, 1, "1 row")));
                await Assert.That(changes).IsEqualTo(1);
            }

            await Assert.That(history.History.Count).IsEqualTo(QueryHistoryStore.MaxEntries);

            history.TogglePinCommand.Execute(history.History[5]);
            var cleared = CountChanges(history.FilteredHistory, () => history.ClearHistoryCommand.Execute(null));

            // Clear removed entries one at a time and rebuilt the filtered list
            // after each: two hundred rebuilds of up to two hundred rows.
            await Assert.That(cleared).IsEqualTo(1);
            await Assert.That(history.History.Count).IsEqualTo(1);

            await history.PendingHistoryWrite;
            await Assert.That(history.HistoryStore.Load().Count).IsEqualTo(1);
        });
    }

    [Test]
    public async Task A_flood_of_notifications_is_two_changes_per_drain_and_keeps_the_selection()
    {
        await Ui.Run(async () =>
        {
            var posted = new List<Action>();
            var monitor = new NotifyMonitorViewModel(new NotificationListener(Fixtures.DataSource), postToUi: posted.Add);

            for (var i = 0; i < NotifyMonitorViewModel.MaxNotifications; i++)
            {
                monitor.Receive(new DatabaseNotification("events", $"{i}", 1, DateTimeOffset.Now));
            }

            posted.Single()();
            posted.Clear();
            var selected = monitor.Notifications[10];
            monitor.SelectedNotification = selected;

            for (var i = 0; i < 300; i++)
            {
                monitor.Receive(new DatabaseNotification("events", $"late {i}", 1, DateTimeOffset.Now));
            }

            // One Remove for the overflow and one Add for the batch; it was a
            // RemoveAt and an Insert(0) per notification.
            var changes = CountChanges(monitor.Notifications, () => posted.Single()());
            await Assert.That(changes).IsEqualTo(2);
            await Assert.That(monitor.Notifications.Count).IsEqualTo(NotifyMonitorViewModel.MaxNotifications);
            await Assert.That(monitor.Notifications[0].Payload).IsEqualTo("late 299");
            await Assert.That(monitor.SelectedNotification).IsSameReferenceAs(selected);
        });
    }

    [Test]
    public async Task Row_details_follow_the_grid_only_while_open()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var tab = vm.ActiveTab;
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "ResultsGrid");

            // Closed: moving through the grid builds no form.
            var changes = CountChanges(tab.RowDetail.Fields, () =>
            {
                grid.SelectedItem = tab.Rows[1];
                Ui.Settle();
                grid.SelectedItem = tab.Rows[2];
                Ui.Settle();
            });
            await Assert.That(changes).IsEqualTo(0);

            // Opening loads the row the grid is on.
            vm.ToggleRowDetailsCommand.Execute(null);
            Ui.Settle();
            await Assert.That(tab.RowDetail.Row).IsSameReferenceAs(tab.Rows[2]);

            // Open: one change per row moved to.
            changes = CountChanges(tab.RowDetail.Fields, () =>
            {
                grid.SelectedItem = tab.Rows[3];
                Ui.Settle();
            });
            await Assert.That(changes).IsEqualTo(1);
            await Assert.That(tab.RowDetail.Row).IsSameReferenceAs(tab.Rows[3]);

            vm.ToggleRowDetailsCommand.Execute(null);
            window.Close();
        });
    }

    [Test]
    public async Task An_edit_context_arriving_after_the_rows_keeps_the_grid_s_columns()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var tab = vm.ActiveTab;
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "ResultsGrid");
            tab.EditContext = null;
            Ui.Settle();
            var before = grid.Columns.ToList();

            // Rebuilding every column for it re-created every realized cell: for a
            // 300-column result that was seconds, after the rows were on screen.
            tab.EditContext = new EditableTableContext("public", "orders", [tab.ColumnNames[0]], []);
            Ui.Settle();

            await Assert.That(grid.Columns.Count).IsEqualTo(before.Count);
            for (var i = 0; i < before.Count; i++)
            {
                await Assert.That(grid.Columns[i]).IsSameReferenceAs(before[i]);
            }

            window.Close();
        });
    }

    [Test]
    public async Task Long_read_only_text_goes_to_the_editor_and_short_text_stays_a_text_block()
    {
        await Ui.Run(async () =>
        {
            var view = new ReadOnlyTextView { Text = "short" };
            var window = new Window { Width = 600, Height = 400, Content = view };
            Ui.Show(window);

            await Assert.That(view.IsShowingEditor).IsFalse();
            await Assert.That(window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.IsEffectivelyVisible).Text).IsEqualTo("short");

            view.Text = string.Concat(Enumerable.Repeat("Seq Scan on events_2026_01  (cost=0.00..1.00 rows=1 width=8)\n", 2_000));
            Ui.Settle();
            await Assert.That(view.IsShowingEditor).IsTrue();
            await Assert.That(window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(b => b.IsEffectivelyVisible)).IsFalse();

            view.Text = "short again";
            Ui.Settle();
            await Assert.That(view.IsShowingEditor).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task A_copy_past_the_limit_is_refused_rather_than_built()
    {
        await Ui.Run(async () =>
        {
            IReadOnlyList<string> columns = ["id", "name"];
            IReadOnlyList<object?[]> rows = [.. Enumerable.Range(0, 1_000).Select(i => new object?[] { i, $"name {i}" })];

            foreach (var format in Enum.GetValues<QueryViewModel.CopyFormat>())
            {
                await Assert.That(QueryViewModel.FormatRows(format, "t", columns, rows, false, maxChars: 1_000)).IsNull();
                await Assert.That(QueryViewModel.FormatRows(format, "t", columns, rows, false, maxChars: 1_000_000)).IsNotNull();
            }
        });
    }
}
