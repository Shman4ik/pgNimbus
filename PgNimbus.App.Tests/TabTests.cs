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
    /// Closing the last tab empties it, Notepad++-style: the tab goes and a
    /// fresh scratch one takes its place, so the window is never left tab-less
    /// (a null ActiveTab that every binding then trips over) and "close" is
    /// never a no-op.
    /// </summary>
    [Test]
    public async Task Closing_the_last_tab_replaces_it_with_an_empty_one()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var only = vm.ActiveTab;
            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.CloseTabCommand.CanExecute(only)).IsTrue();
            await Assert.That(only.Rows).IsNotEmpty();

            vm.CloseTabCommand.Execute(only);
            Ui.Settle();

            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.Tabs).DoesNotContain(only);
            await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[0]);
            // A scratch tab as if freshly opened: the closed tab's result and
            // its name are both gone, and it is "Query 1" because it is the
            // only one, whatever the session's tab count reached before.
            await Assert.That(vm.ActiveTab.Rows).IsEmpty();
            await Assert.That(vm.ActiveTab.TabTitle).IsEqualTo("Query 1");

            window.Close();
        });
    }

    /// <summary>
    /// A tab named by hand keeps that name through later edits — the whole
    /// point of renaming — while an app-assigned label does not (see
    /// <see cref="A_tab_labelled_by_its_content_follows_the_content"/>).
    /// </summary>
    [Test]
    public async Task Renaming_a_tab_sticks_and_a_blank_name_restores_the_automatic_one()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var tab = vm.ActiveTab;
            vm.RenameTabCommand.Execute(tab);
            Ui.Settle();

            await Assert.That(tab.IsRenaming).IsTrue();
            await Assert.That(tab.RenameText).IsEqualTo(tab.TabTitle);

            tab.RenameText = "Nightly report";
            tab.CommitRename();
            Ui.Settle();

            await Assert.That(tab.IsRenaming).IsFalse();
            await Assert.That(tab.TabTitle).IsEqualTo("Nightly report");

            // The name a person chose outranks the SQL-derived one from then on.
            tab.Sql = "SELECT * FROM orders";
            await Assert.That(tab.TabTitle).IsEqualTo("Nightly report");

            // Clearing it hands the tab back to automatic naming.
            tab.BeginRename();
            tab.RenameText = "   ";
            tab.CommitRename();
            await Assert.That(tab.TabTitle).IsEqualTo("orders");

            window.Close();
        });
    }

    [Test]
    public async Task Cancelling_a_rename_leaves_the_name_alone()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var tab = vm.ActiveTab;
            var before = tab.TabTitle;

            tab.BeginRename();
            tab.RenameText = "Something else";
            tab.CancelRename();
            Ui.Settle();

            await Assert.That(tab.IsRenaming).IsFalse();
            await Assert.That(tab.TabTitle).IsEqualTo(before);

            window.Close();
        });
    }

    /// <summary>
    /// A tab opened by browsing a table is *labelled* after it, not renamed to
    /// it: typing a different query in that tab must retitle it after what it
    /// now selects from, instead of leaving the old table's name on a tab that
    /// no longer has anything to do with it.
    /// </summary>
    [Test]
    public async Task A_tab_labelled_by_its_content_follows_the_content()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var tab = vm.ActiveTab;
            tab.DefaultTitle = "customers";
            tab.Sql = "";
            await Assert.That(tab.TabTitle).IsEqualTo("customers");

            tab.Sql = "select * from commerce.products";
            await Assert.That(tab.TabTitle).IsEqualTo("products");

            window.Close();
        });
    }
}
