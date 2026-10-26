using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Query Lab end to end on the headless platform: the plan header's Compare menu, saving
/// what a tab shows, comparing with no server, the list's actions, and that the window
/// opens once per main window and stays light on a plan of thousands of nodes.
/// </summary>
public class QueryLabTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static QueryLabViewModel Inline(QueryLabViewModel lab)
    {
        lab.OffUiThread = work =>
        {
            work();
            return Task.CompletedTask;
        };
        return lab;
    }

    private static PlanCapture Capture(string planText, SavedPlanSource source = SavedPlanSource.Imported) =>
        new(ExplainService.Import(planText).Result, planText, source, null, null, new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero));

    private static IEnumerable<PlanDiffNodeViewModel> All(IEnumerable<PlanDiffNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));

    [Test]
    public async Task The_compare_menu_saves_lists_saved_plans_and_opens_the_lab()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Inline(vm.QueryLab);
            Ui.Show(window);
            vm.OpenImportedPlan(ExplainService.Import(Fixture("plan-after-index.json")));
            Ui.Settle();

            var chip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ComparePlanButton");
            await Assert.That(chip.IsEffectivelyVisible).IsTrue();

            IReadOnlyList<object?> OpenMenu()
            {
                var flyout = (MenuFlyout)chip.Flyout!;
                flyout.ShowAt(chip);
                Ui.Settle();
                var items = flyout.Items.ToList();
                flyout.Hide();
                Ui.Settle();
                return items;
            }

            var menu = OpenMenu();
            await Assert.That(menu.OfType<MenuItem>().Select(i => (i.Header as string)!))
                .IsEquivalentTo(["Save to Query Lab…", "Compare With", "Open Query Lab"], CollectionOrdering.Matching);
            var compareWith = menu.OfType<MenuItem>().Single(i => (string?)i.Header == "Compare With");
            var none = compareWith.Items.OfType<MenuItem>().First();
            await Assert.That(none.Header).IsEqualTo("No saved plans");
            await Assert.That(none.IsEnabled).IsFalse();

            await vm.QueryLab.SaveAsync(Capture(Fixture("plan-before-index.json")), "no_index baseline", null);
            compareWith = OpenMenu().OfType<MenuItem>().Single(i => (string?)i.Header == "Compare With");
            // An underscore in a header is an access key unless it is doubled.
            await Assert.That(compareWith.Items.OfType<MenuItem>().First().Header).IsEqualTo("no__index baseline");

            window.Close();
        });
    }

    [Test]
    public async Task Compare_with_puts_the_saved_plan_before_and_the_tabs_plan_after()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            var lab = Inline(vm.QueryLab);
            Ui.Show(window);
            var baseline = await lab.SaveAsync(Capture(Fixture("plan-before-index.json"), SavedPlanSource.Explain), "baseline", null);

            vm.OpenImportedPlan(ExplainService.Import(Fixture("plan-after-index.json")));
            await vm.ComparePlanWithAsync(baseline);
            await lab.PendingComparison;
            Ui.Settle();

            await Assert.That(lab.Before!.Saved!.Id).IsEqualTo(baseline!.Id);
            await Assert.That(lab.After!.IsUnsaved).IsTrue();
            await Assert.That(lab.After.Label).StartsWith("Unsaved: the plan in");
            await Assert.That(lab.Comparison).IsNotNull();
            await Assert.That(lab.Comparison!.Notes.Select(n => n.Title)).Contains("One run each");
            await Assert.That(lab.Comparison.ChangeSummary).IsEqualTo("1 changed · 1 added · 1 removed");

            // One lab per main window: asking again only brings it forward.
            vm.ShowQueryLabCommand.Execute(null);
            Ui.Settle();
            var labs = window.OwnedWindows.OfType<QueryLabWindow>().ToList();
            await Assert.That(labs.Count).IsEqualTo(1);

            labs[0].Close();
            Ui.Settle();
            await Assert.That(window.OwnedWindows.OfType<QueryLabWindow>().Any()).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task Two_imported_plans_compare_with_no_server()
    {
        await Ui.Run(async () =>
        {
            var lab = Inline(Fixtures.MainWindowViewModel().QueryLab);
            var imports = new Queue<string>([Fixture("plan-before-index.json"), Fixture("plan-after-index.json")]);
            var names = new Queue<string>(["before", "after"]);
            lab.PickImport = () => Task.FromResult<ImportedPlan?>(ExplainService.Import(imports.Dequeue()));
            lab.AskName = (_, _, _) => Task.FromResult<(string, Guid?)?>((names.Dequeue(), null));

            await lab.ImportPlanCommand.ExecuteAsync(null);
            await lab.ImportPlanCommand.ExecuteAsync(null);
            lab.Before = lab.Choices.Single(c => c.Label == "before");
            lab.After = lab.Choices.Single(c => c.Label == "after");
            await lab.PendingComparison;

            await Assert.That(lab.Plans.Select(p => p.Plan.Source).Distinct()).IsEquivalentTo([SavedPlanSource.Imported]);
            await Assert.That(lab.Comparison).IsNotNull();
            // Imports carry no connection, so none differs.
            await Assert.That(lab.Comparison!.Notes.Select(n => n.Title)).DoesNotContain("Different connections");
            var scan = All(lab.Comparison.Roots).Single(n => n.IsChanged);
            await Assert.That(scan.WasHeader).IsEqualTo("was Parallel Seq Scan on orders o");
        });
    }

    [Test]
    public async Task Delete_asks_first_and_rename_keeps_the_plan()
    {
        await Ui.Run(async () =>
        {
            var lab = Inline(Fixtures.MainWindowViewModel().QueryLab);
            await lab.SaveAsync(Capture(Fixture("plan-after-index.json")), "kept", null);
            var item = lab.Plans.Single();

            var answer = false;
            lab.ConfirmDelete = _ => Task.FromResult(answer);
            await lab.DeleteCommand.ExecuteAsync(item);
            await Assert.That(lab.Plans.Count).IsEqualTo(1);

            lab.AskName = (_, current, _) => Task.FromResult<(string, Guid?)?>((current + " (renamed)", null));
            await lab.RenameCommand.ExecuteAsync(lab.Plans.Single());
            await Assert.That(lab.Plans.Single().Name).IsEqualTo("kept (renamed)");

            answer = true;
            await lab.DeleteCommand.ExecuteAsync(lab.Plans.Single());
            await Assert.That(lab.Plans).IsEmpty();
            await Assert.That(lab.Status).IsEqualTo("Deleted “kept (renamed)”");
        });
    }

    [Test]
    public async Task Swap_exchanges_the_sides_and_compares_again()
    {
        await Ui.Run(async () =>
        {
            var (lab, _) = Scenarios.SeededQueryLab();
            await lab.PendingComparison;
            var before = lab.Before;

            lab.SwapCommand.Execute(null);
            await lab.PendingComparison;

            await Assert.That(lab.After).IsSameReferenceAs(before);
            await Assert.That(lab.Comparison!.ChangeSummary).IsEqualTo("1 changed · 1 added · 1 removed");
            await Assert.That(All(lab.Comparison.Roots).Single(n => n.IsAdded).Header).IsEqualTo("Gather");
        });
    }

    [Test]
    public async Task A_saved_plan_opens_in_a_new_tab_with_its_statement_as_comments()
    {
        await Ui.Run(async () =>
        {
            var (lab, vm) = Scenarios.SeededQueryLab();
            var tabs = vm.Tabs.Count;
            var item = lab.Plans.First();

            await lab.OpenInNewTabCommand.ExecuteAsync(item);

            await Assert.That(vm.Tabs.Count).IsEqualTo(tabs + 1);
            await Assert.That(vm.ActiveTab.TabTitle).IsEqualTo(item.Name);
            await Assert.That(vm.ActiveTab.IsShowingPlan).IsTrue();
            await Assert.That(vm.ActiveTab.PlanCapture!.Saved!.Id).IsEqualTo(item.Plan.Id);
            // A saved plan can be of a write: none of its statement may run on Ctrl+Enter.
            var lines = vm.ActiveTab.Sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.All(l => l.StartsWith("--", StringComparison.Ordinal))).IsTrue();
            await Assert.That(vm.ActiveTab.Sql).Contains("WHERE o.customer_id = 42");
        });
    }

    [Test]
    public async Task The_capture_goes_with_the_plan_and_save_needs_one()
    {
        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel();
            PlanCapture? asked = null;
            vm.SavePlanRequested += capture => asked = capture;

            vm.SavePlanToQueryLabCommand.Execute(null);
            await Assert.That(asked).IsNull();
            await Assert.That(vm.ActiveTab.HasError).IsTrue();

            vm.OpenImportedPlan(ExplainService.Import(Fixture("plan-after-index.json")));
            vm.SavePlanToQueryLabCommand.Execute(null);
            await Assert.That(asked).IsSameReferenceAs(vm.ActiveTab.PlanCapture);

            // Typing hides the plan, and with it what Save would record.
            vm.ActiveTab.Sql = "SELECT 2;";
            await Assert.That(vm.ActiveTab.PlanCapture).IsNull();
        });
    }

    [Test]
    public async Task A_diff_of_thousands_of_nodes_realizes_only_the_rows_on_screen()
    {
        await Ui.Run(async () =>
        {
            var lab = Inline(Fixtures.MainWindowViewModel().QueryLab);
            await lab.SaveAsync(Capture(Partitions(0, 2000)), "all partitions", null);
            await lab.SaveAsync(Capture(Partitions(0, 1990)), "pruned", null);
            lab.Before = lab.Choices.Single(c => c.Label == "all partitions");
            lab.After = lab.Choices.Single(c => c.Label == "pruned");
            await lab.PendingComparison;
            lab.SelectedTab = 1;

            var window = new QueryLabWindow { DataContext = lab, Width = 1180, Height = 720 };
            Ui.Show(window);
            var append = All(lab.Comparison!.Roots).Single(n => n.Header == "Append");
            append.IsExpanded = true;
            Ui.Settle(passes: 4);

            var rows = window.GetVisualDescendants().OfType<TreeViewItem>().Count();
            await Assert.That(append.Children.Count).IsEqualTo(1991);
            await Assert.That(rows).IsLessThan(200);
            window.Close();
        });
    }

    /// <summary>A FORMAT JSON plan of an Append over partitions <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static string Partitions(int from, int to)
    {
        var json = new StringBuilder("""[{"Plan": {"Node Type": "Append", "Total Cost": 100, "Plan Rows": 1000, "Plan Width": 8, "Plans": [""");
        for (var p = from; p < to; p++)
        {
            json.Append(p > from ? "," : "")
                .Append($$"""{"Node Type": "Seq Scan", "Parent Relationship": "Member", "Relation Name": "orders_p{{p}}", "Alias": "orders_p{{p}}", "Total Cost": 1, "Plan Rows": 1, "Plan Width": 8}""");
        }

        return json.Append("]}}]").ToString();
    }
}
