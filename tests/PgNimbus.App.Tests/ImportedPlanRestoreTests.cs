using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Core.Settings;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// An imported plan is the tab's content: there is no query behind it to run
/// again. The 1.0.1 release pass found it coming back after a restart as an
/// empty "Query N" holding only the import's comment, because the snapshot kept
/// the tab's text and its override name, and the plan and its label were
/// neither. Reopen Closed Tab lost the plan the same way.
/// </summary>
public class ImportedPlanRestoreTests
{
    private const string TextPlan =
        "Sort  (cost=10.00..10.11 rows=5 width=4) (actual time=900.1..900.2 rows=5000 loops=1)\n"
        + "  ->  Seq Scan on orders  (cost=0.00..5.00 rows=5 width=4) (actual time=0.01..450.0 rows=5000 loops=1)";

    private const string JsonPlan =
        "[{\"Plan\": {\"Node Type\": \"Seq Scan\", \"Relation Name\": \"orders\", \"Startup Cost\": 0.0, "
        + "\"Total Cost\": 5.0, \"Plan Rows\": 5, \"Plan Width\": 4}}]";

    // Closes the window the way App does: the snapshot through the real store,
    // read back by the next window's view model.
    private static async Task<MainViewModel> RestartAsync(MainViewModel vm)
    {
        var store = new WorkspaceStore(Path.Combine(IsolatedAppData.NewDirectory("imported-plan"), "workspace.json"));
        store.Save("localhost/shop", [.. vm.Tabs.Select(t => t.ToWorkspaceTab())], vm.Tabs.IndexOf(vm.ActiveTab));
        var restored = Fixtures.MainWindowViewModel(store.GetEntry("localhost/shop"));
        await restored.WorkspacePlansRestored.WaitAsync(TimeSpan.FromSeconds(10));
        return restored;
    }

    [Test]
    [Arguments(TextPlan, false)]
    [Arguments(JsonPlan, true)]
    public async Task An_imported_plan_comes_back_after_a_restart(string pasted, bool isJson)
    {
        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel();
            vm.OpenImportedPlan(ExplainService.Import(pasted));

            var restored = await RestartAsync(vm);
            Ui.Settle();

            var tab = restored.ActiveTab;
            await Assert.That(tab.TabTitle).IsEqualTo("Imported plan");
            await Assert.That(tab.IsShowingPlan).IsTrue();
            await Assert.That(tab.ExplainRoot).IsNotNull();
            // A JSON import keeps its JSON export; a text one never had it.
            await Assert.That(tab.HasPlanJson).IsEqualTo(isJson);
            // A label, not a name someone chose: it still yields to typed SQL.
            await Assert.That(tab.TitleOverride).IsNull();
        });
    }

    [Test]
    public async Task A_plan_put_aside_is_not_kept()
    {
        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel();
            vm.OpenImportedPlan(ExplainService.Import(TextPlan));
            // Typing hides the plan, as Run and the plan header's ✕ do; after
            // that the tab is the query being typed.
            vm.ActiveTab.Sql = "SELECT * FROM orders;";
            await Assert.That(vm.ActiveTab.ToWorkspaceTab().ImportedPlan).IsNull();

            var restored = await RestartAsync(vm);
            Ui.Settle();

            await Assert.That(restored.ActiveTab.IsShowingPlan).IsFalse();
            await Assert.That(restored.ActiveTab.TabTitle).IsNotEqualTo("Imported plan");
        });
    }

    [Test]
    public async Task A_pasted_plan_that_no_longer_parses_leaves_a_named_tab_and_says_why()
    {
        await Ui.Run(async () =>
        {
            var workspace = new WorkspaceEntry("localhost/shop", DateTimeOffset.UtcNow,
                [new WorkspaceTab("-- Imported plan\n", ImportedPlan: "[{\"Plan\": 5}]")]);

            var vm = Fixtures.MainWindowViewModel(workspace);
            await vm.WorkspacePlansRestored.WaitAsync(TimeSpan.FromSeconds(10));
            Ui.Settle();

            await Assert.That(vm.ActiveTab.TabTitle).IsEqualTo("Imported plan");
            await Assert.That(vm.ActiveTab.IsShowingPlan).IsFalse();
            await Assert.That(vm.ActiveTab.Status).Contains("could not be read");
        });
    }

    [Test]
    public async Task Reopen_closed_tab_brings_the_plan_back()
    {
        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel();
            vm.OpenImportedPlan(ExplainService.Import(JsonPlan));
            vm.CloseTabCommand.Execute(vm.ActiveTab);
            vm.ReopenClosedTabCommand.Execute(null);
            Ui.Settle();

            await Assert.That(vm.ActiveTab.TabTitle).IsEqualTo("Imported plan");
            await Assert.That(vm.ActiveTab.IsShowingPlan).IsTrue();
            await Assert.That(vm.ActiveTab.HasPlanJson).IsTrue();
        });
    }
}
