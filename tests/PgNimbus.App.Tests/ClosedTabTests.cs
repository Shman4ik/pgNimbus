using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Close is undoable. Cmd/Ctrl+W used to take a tab holding unsaved typed SQL
/// with no prompt and no way back; now every tab with something in it is kept
/// for Reopen Closed Tab (Cmd/Ctrl+Shift+T), and closing one whose SQL is saved
/// nowhere says so, naming the gesture. On macOS the same Cmd+W, once the
/// window's only tab is empty, closes the window, as it does in every Mac app.
/// </summary>
public class ClosedTabTests
{
    private const string Typed = "SELECT id, total\nFROM orders\nWHERE total > 100;";

    [Test]
    public async Task Reopen_brings_back_the_text_name_link_and_place_of_a_closed_tab()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var first = vm.ActiveTab;
            vm.AddTabCommand.Execute(null);
            var closing = vm.ActiveTab;
            closing.Sql = Typed;
            closing.TitleOverride = "Big orders";
            var savedId = Guid.NewGuid();
            closing.SavedQueryId = savedId;
            vm.AddTabCommand.Execute(null);
            Ui.Settle();

            vm.CloseTabCommand.Execute(closing);
            Ui.Settle();
            await Assert.That(vm.Tabs).DoesNotContain(closing);
            await Assert.That(vm.ClosedTabs).Count().IsEqualTo(1);
            await Assert.That(vm.ReopenClosedTabCommand.CanExecute(null)).IsTrue();

            // Through the real gesture, so the binding is covered too.
            Ui.Press(window, CommandId.ReopenClosedTab);

            var reopened = vm.ActiveTab;
            await Assert.That(reopened).IsNotEqualTo(closing);
            await Assert.That(reopened.Sql).IsEqualTo(Typed);
            await Assert.That(reopened.TabTitle).IsEqualTo("Big orders");
            await Assert.That(reopened.SavedQueryId).IsEqualTo(savedId);
            // Back where it was: between the first tab and the one after it.
            await Assert.That(vm.Tabs.IndexOf(reopened)).IsEqualTo(1);
            await Assert.That(vm.Tabs[0]).IsEqualTo(first);
            await Assert.That(vm.ClosedTabs).IsEmpty();
            await Assert.That(vm.ReopenClosedTabCommand.CanExecute(null)).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task A_reopened_tab_puts_the_caret_back_where_it_was()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");

            vm.AddTabCommand.Execute(null);
            Ui.Settle();
            var tab = vm.ActiveTab;
            tab.Sql = Typed;
            Ui.Settle();
            var caret = Typed.IndexOf("orders", StringComparison.Ordinal);
            editor.CaretOffset = caret;
            Ui.Settle();
            await Assert.That(tab.CaretOffset).IsEqualTo(caret);

            vm.CloseTabCommand.Execute(tab);
            Ui.Settle();
            await Assert.That(editor.CaretOffset).IsNotEqualTo(caret);

            vm.ReopenClosedTabCommand.Execute(null);
            Ui.Settle();

            await Assert.That(editor.Text).IsEqualTo(Typed);
            await Assert.That(editor.CaretOffset).IsEqualTo(caret);
            await Assert.That(vm.ActiveTab.PendingCaretOffset).IsNull();

            window.Close();
        });
    }

    /// <summary>
    /// A file tab comes back attached to its file, with the dirty dot it had:
    /// the baseline is kept with the closed tab, so nothing reads the disk.
    /// </summary>
    [Test]
    public async Task A_reopened_file_tab_is_still_its_file_and_still_dirty()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var path = Path.Combine(Path.GetTempPath(), "never-read-" + Guid.NewGuid().ToString("N") + ".sql");
            vm.AddTabCommand.Execute(null);
            var tab = vm.ActiveTab;
            tab.Sql = "SELECT 1 AS on_disk;";
            tab.AttachFile(path, tab.Sql);
            tab.Sql = "SELECT 2 AS edited;";
            await Assert.That(tab.IsDirty).IsTrue();

            vm.CloseTabCommand.Execute(tab);
            vm.ReopenClosedTabCommand.Execute(null);
            Ui.Settle();

            var reopened = vm.ActiveTab;
            await Assert.That(reopened.FilePath).IsEqualTo(path);
            await Assert.That(reopened.TabTitle).IsEqualTo(Path.GetFileName(path));
            await Assert.That(reopened.Sql).IsEqualTo("SELECT 2 AS edited;");
            await Assert.That(reopened.IsDirty).IsTrue();

            // Put back what was on disk and the dot goes, so the baseline came
            // back too, not just the flag.
            reopened.Sql = "SELECT 1 AS on_disk;";
            await Assert.That(reopened.IsDirty).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task Close_others_and_close_to_the_right_keep_every_tab_they_close()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var keep = vm.ActiveTab;
            foreach (var name in new[] { "a", "b", "c" })
            {
                vm.AddTabCommand.Execute(null);
                vm.ActiveTab.Sql = $"SELECT '{name}';";
            }

            Ui.Settle();
            vm.CloseOtherTabsCommand.Execute(keep);
            Ui.Settle();

            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.ClosedTabs.Select(c => c.Sql)).IsEquivalentTo(["SELECT 'a';", "SELECT 'b';", "SELECT 'c';"]);
            // One line for the lot, not the last close's line.
            await Assert.That(keep.Status).Contains("Closed 3 tabs");

            // Most recent first: the rightmost comes back first, in its place.
            vm.ReopenClosedTabCommand.Execute(null);
            await Assert.That(vm.ActiveTab.Sql).IsEqualTo("SELECT 'c';");
            vm.ReopenClosedTabCommand.Execute(null);
            vm.ReopenClosedTabCommand.Execute(null);
            Ui.Settle();
            await Assert.That(vm.Tabs.Select(t => t.Sql)).IsEquivalentTo(
                [keep.Sql, "SELECT 'a';", "SELECT 'b';", "SELECT 'c';"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

            vm.CloseTabsToTheRightCommand.Execute(keep);
            Ui.Settle();
            await Assert.That(vm.ClosedTabs).Count().IsEqualTo(3);

            window.Close();
        });
    }

    /// <summary>
    /// Closing a tab whose SQL is saved nowhere says so, with the gesture that
    /// brings it back, read from the catalog in the live Ctrl/Cmd scheme.
    /// </summary>
    [Test]
    public async Task Closing_unsaved_sql_says_how_to_get_it_back()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.AddTabCommand.Execute(null);
            var tab = vm.ActiveTab;
            tab.Sql = Typed;
            Ui.Settle();

            vm.CloseTabCommand.Execute(tab);
            Ui.Settle();

            var gesture = CommandCatalog.ChordFor(CommandId.ReopenClosedTab, Hotkeys.Scheme)!.Value.Label(Hotkeys.Scheme);
            await Assert.That(vm.ActiveTab.Status).Contains(tab.TabTitle);
            await Assert.That(vm.ActiveTab.Status).Contains("unsaved");
            await Assert.That(vm.ActiveTab.Status).Contains(gesture);

            window.Close();
        });
    }

    /// <summary>
    /// A tab that is exactly its saved query, or an untouched scratch tab, has
    /// nothing to lose: the first is kept for reopening but raises no note, the
    /// second is not kept at all (reopening it would bring back nothing).
    /// </summary>
    [Test]
    public async Task Tabs_with_nothing_to_lose_close_quietly()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            // Straight into the collection rather than through SaveQuery, which
            // would write the list to the real app-data file.
            var entry = new PgNimbus.Core.Query.SavedQuery(Guid.NewGuid(), "Daily report", "SELECT 42;");
            vm.SavedQueries.SavedQueries.Add(entry);
            vm.AddTabCommand.Execute(null);
            var saved = vm.ActiveTab;
            saved.Sql = entry.Sql;
            saved.MarkSavedAsQuery(entry.Id, entry.Name);
            vm.AddTabCommand.Execute(null);
            var scratch = vm.ActiveTab;
            Ui.Settle();
            await Assert.That(scratch.IsUntouchedScratch).IsTrue();

            vm.CloseTabCommand.Execute(scratch);
            Ui.Settle();
            await Assert.That(vm.ClosedTabs).IsEmpty();

            var survivor = vm.Tabs[0];
            var before = survivor.Status;
            vm.CloseTabCommand.Execute(saved);
            Ui.Settle();
            await Assert.That(vm.ClosedTabs).Count().IsEqualTo(1);
            await Assert.That(vm.ActiveTab).IsEqualTo(survivor);
            await Assert.That(survivor.Status).IsEqualTo(before);

            vm.SavedQueries.SavedQueries.Remove(entry);
            window.Close();
        });
    }

    [Test]
    public async Task The_stack_keeps_the_most_recent_twenty()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var tabs = new List<QueryViewModel>();
            for (var i = 0; i < MainViewModel.MaxClosedTabs + 5; i++)
            {
                vm.AddTabCommand.Execute(null);
                vm.ActiveTab.Sql = $"SELECT {i};";
                tabs.Add(vm.ActiveTab);
            }

            foreach (var tab in tabs)
            {
                vm.CloseTabCommand.Execute(tab);
            }

            Ui.Settle();
            await Assert.That(vm.ClosedTabs).Count().IsEqualTo(MainViewModel.MaxClosedTabs);
            await Assert.That(vm.ClosedTabs[0].Sql).IsEqualTo("SELECT 5;");
            await Assert.That(vm.ClosedTabs[^1].Sql).IsEqualTo($"SELECT {MainViewModel.MaxClosedTabs + 4};");

            window.Close();
        });
    }

    /// <summary>
    /// Closing the last tab leaves an empty "Query 1" in its place; reopening
    /// the closed one then replaces that placeholder rather than sitting beside
    /// it.
    /// </summary>
    [Test]
    public async Task Reopening_after_closing_the_last_tab_replaces_the_empty_one()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            vm.CloseWindowWithLastEmptyTab = false;
            Ui.Show(window);

            var only = vm.ActiveTab;
            var sql = only.Sql;
            vm.CloseTabCommand.Execute(only);
            Ui.Settle();
            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.ActiveTab.IsUntouchedScratch).IsTrue();

            vm.ReopenClosedTabCommand.Execute(null);
            Ui.Settle();

            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.ActiveTab.Sql).IsEqualTo(sql);

            window.Close();
        });
    }

    /// <summary>
    /// The Mac convention: Cmd+W with a tab that has something in it empties
    /// the tab (the content can come back), and Cmd+W again, on the now-empty
    /// only tab, closes the window. The app keeps running either way there.
    /// </summary>
    [Test]
    public async Task On_a_Mac_closing_the_only_empty_tab_closes_the_window()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            vm.CloseWindowWithLastEmptyTab = true;
            Ui.Show(window);
            var closed = false;
            window.Closed += (_, _) => closed = true;

            Ui.Press(window, CommandId.CloseTab);
            await Assert.That(closed).IsFalse();
            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.ActiveTab.IsUntouchedScratch).IsTrue();
            await Assert.That(vm.ClosedTabs).Count().IsEqualTo(1);

            Ui.Press(window, CommandId.CloseTab);
            await Assert.That(closed).IsTrue();
        });
    }

    /// <summary>Windows and Linux keep "closing the last tab empties it": there, closing the last window would quit.</summary>
    [Test]
    public async Task Elsewhere_closing_the_only_empty_tab_leaves_the_window_open()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            vm.CloseWindowWithLastEmptyTab = false;
            Ui.Show(window);
            var closed = false;
            window.Closed += (_, _) => closed = true;

            Ui.Press(window, CommandId.CloseTab);
            Ui.Press(window, CommandId.CloseTab);

            await Assert.That(closed).IsFalse();
            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.ActiveTab.IsUntouchedScratch).IsTrue();

            window.Close();
        });
    }
}
