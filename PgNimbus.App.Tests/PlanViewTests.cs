using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// How a plan arrives on screen. The 0.14.0 release pass found the heat-mapped
/// tree, the point of the plan view, hidden twice over: every plan opened as
/// text however often the tree was picked, and the tree itself opened as one
/// collapsed root.
/// </summary>
public class PlanViewTests
{
    private const string TextPlan =
        "Sort  (cost=10.00..10.11 rows=5 width=4) (actual time=900.1..900.2 rows=5000 loops=1)\n"
        + "  ->  Seq Scan on orders  (cost=0.00..5.00 rows=5 width=4) (actual time=0.01..450.0 rows=5000 loops=1)";

    [Test]
    public async Task A_new_tab_opens_its_plan_in_the_view_last_chosen()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            // Text stays the default until someone picks the tree.
            await Assert.That(vm.ActiveTab.IsPlanTextView).IsTrue();

            vm.ActiveTab.ShowPlanAsTreeCommand.Execute(null);
            vm.OpenImportedPlan(ExplainService.Import(TextPlan));
            Ui.Settle();
            await Assert.That(vm.ActiveTab.IsPlanTextView).IsFalse();

            vm.ActiveTab.ShowPlanAsTextCommand.Execute(null);
            vm.AddTabCommand.Execute(null);
            Ui.Settle();
            await Assert.That(vm.ActiveTab.IsPlanTextView).IsTrue();
            await Assert.That(vm.PlanTreeView).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task The_plan_tree_opens_with_every_node_expanded()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.QueryPlanTree();
            Ui.Show(window);

            var items = window.GetVisualDescendants().OfType<TreeViewItem>()
                .Where(i => i.DataContext is ExplainNodeViewModel)
                .ToList();

            // More than the root on screen at all is the first half of the point.
            await Assert.That(items.Count).IsGreaterThan(1);
            await Assert.That(items.Where(i => i.ItemCount > 0).All(i => i.IsExpanded)).IsTrue();

            window.Close();
        });
    }

    [Test]
    public async Task An_imported_plan_tab_does_not_carry_a_query_to_run()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.OpenImportedPlan(ExplainService.Import(TextPlan));
            Ui.Settle();

            // The new tab's usual SELECT 1 read as the plan's own query, and Run
            // swapped the plan for its one-row result.
            await Assert.That(vm.ActiveTab.Sql.TrimStart().StartsWith("--", StringComparison.Ordinal)).IsTrue();
            await Assert.That(vm.ActiveTab.TabTitle).IsEqualTo("Imported plan");
            await Assert.That(vm.ActiveTab.IsShowingPlan).IsTrue();

            window.Close();
        });
    }

    // --- Security audit 2026-09, finding 16: a plan that cannot be read is a message, not a crash ---

    [Test]
    [Arguments("[{\"Plan\": 5}]")]
    [Arguments("[{\"Plan\": {\"Startup Cost\": 1}}]")]
    [Arguments("Seq Scan on t  (cost=0.00..1.00 rows=99999999999999999999 width=4)\n  ->  Bogus line with no cost")]
    public async Task The_import_dialog_reports_a_plan_it_cannot_read_and_stays_open(string raw)
    {
        // Each of these used to escape the Import click as something other than a
        // FormatException (InvalidOperation, KeyNotFound, Overflow) and reach the
        // dispatcher's unhandled-exception hook: crash window, app shut down.
        await Ui.Run(async () =>
        {
            var dialog = new ImportPlanDialog();
            Ui.Show(dialog);

            var input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            input.Text = raw;
            var import = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Import");
            import.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Ui.Settle();

            var error = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ErrorText");
            await Assert.That(error.IsVisible).IsTrue();
            await Assert.That(string.IsNullOrWhiteSpace(error.Text)).IsFalse();
            await Assert.That(dialog.IsVisible).IsTrue();

            dialog.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_run_explain_whose_output_is_not_a_plan_keeps_its_rows()
    {
        // The Run path parses QUERY PLAN rows back through the importer; a server
        // answering EXPLAIN with something unreadable leaves the rows in the grid.
        await Ui.Run(async () =>
        {
            var section = ScriptResultViewModel.From(1, "EXPLAIN (FORMAT JSON) SELECT 1", new MaterializedResultSet
            {
                Elapsed = TimeSpan.FromMilliseconds(1),
                Columns = [new ColumnInfo("QUERY PLAN", "json", typeof(string))],
                Rows = [["[{\"Plan\": 5}]"]],
            });

            await Assert.That(section.Plan).IsNull();
            await Assert.That(section.Rows.Count).IsEqualTo(1);
        });
    }

    [Test]
    public async Task The_cheat_sheet_draws_the_tab_number_range_as_keys()
    {
        await Ui.Run(async () =>
        {
            var row = new ShortcutsViewModel().Sections
                .SelectMany(s => s.Rows)
                .Single(r => r.Action.StartsWith("Go to tab", StringComparison.Ordinal));

            await Assert.That(row.Tokens.Count).IsEqualTo(2);
            await Assert.That(row.Tokens.All(t => t.IsKey)).IsTrue();
            await Assert.That(row.Tokens[1].Text).IsEqualTo("1…9");
        });
    }
}
