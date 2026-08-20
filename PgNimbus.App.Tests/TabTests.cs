using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The tab strip's contract. Two of these encode UI design rules that were
/// decided once and are easy to break by accident later.
/// </summary>
public class TabTests
{
    /// <summary>
    /// UI design rule 3: loading a saved query opens a *new* tab. The rule
    /// exists because overwriting the tab someone was typing in loses their
    /// work, so this asserts on the original tab's SQL, not just on the count.
    /// </summary>
    [Test]
    public async Task Opening_a_saved_query_never_overwrites_the_active_tab()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var original = vm.ActiveTab;
            var originalSql = original.Sql;
            var saved = vm.SavedQueries.SavedQueries[0];

            vm.SavedQueries.LoadSavedQueryCommand.Execute(saved);
            Ui.Settle();

            await Assert.That(vm.Tabs).Count().IsEqualTo(2);
            await Assert.That(original.Sql).IsEqualTo(originalSql);
            await Assert.That(vm.ActiveTab).IsNotEqualTo(original);
            await Assert.That(vm.ActiveTab.Sql).IsEqualTo(saved.Sql);

            window.Close();
        });
    }

    [Test]
    public async Task New_tab_becomes_active_and_leaves_the_others_alone()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var first = vm.ActiveTab;
            vm.AddTabCommand.Execute(null);
            Ui.Settle();

            await Assert.That(vm.Tabs).Count().IsEqualTo(2);
            await Assert.That(vm.ActiveTab).IsNotEqualTo(first);
            await Assert.That(vm.Tabs).Contains(first);

            window.Close();
        });
    }

    /// <summary>
    /// "Close others" acts on the tab it is given, which is the *clicked* one and
    /// not necessarily the active one — the whole reason the command takes a
    /// parameter.
    /// </summary>
    [Test]
    public async Task Close_others_keeps_only_the_targeted_tab()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.AddTabCommand.Execute(null);
            vm.AddTabCommand.Execute(null);
            vm.AddTabCommand.Execute(null);
            Ui.Settle();
            await Assert.That(vm.Tabs).Count().IsEqualTo(4);

            var keep = vm.Tabs[1];
            vm.CloseOtherTabsCommand.Execute(keep);
            Ui.Settle();

            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.Tabs[0]).IsEqualTo(keep);
            await Assert.That(vm.ActiveTab).IsEqualTo(keep);

            window.Close();
        });
    }

    [Test]
    public async Task Close_to_the_right_keeps_everything_up_to_the_target()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.AddTabCommand.Execute(null);
            vm.AddTabCommand.Execute(null);
            vm.AddTabCommand.Execute(null);
            Ui.Settle();

            var target = vm.Tabs[1];
            vm.CloseTabsToTheRightCommand.Execute(target);
            Ui.Settle();

            await Assert.That(vm.Tabs).Count().IsEqualTo(2);
            await Assert.That(vm.Tabs[1]).IsEqualTo(target);

            window.Close();
        });
    }

    /// <summary>
    /// Closing the last tab must leave a usable window rather than a null
    /// ActiveTab that every binding then trips over.
    /// </summary>
    [Test]
    public async Task Closing_the_last_tab_leaves_a_live_tab_behind()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.CloseTabCommand.Execute(vm.ActiveTab);
            Ui.Settle();

            await Assert.That(vm.Tabs).IsNotEmpty();
            await Assert.That(vm.ActiveTab).IsNotNull();

            window.Close();
        });
    }
}
