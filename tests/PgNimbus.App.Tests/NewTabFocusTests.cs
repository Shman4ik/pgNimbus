using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A tab the user asks for takes keyboard focus into its editor. Found on the
/// NativeAOT Windows build (2026-10-01): Ctrl+T opened a tab and typing "abc"
/// went nowhere until the editor was clicked, unlike every editor and browser.
/// Only the explicit commands do it (New Query Tab, Reopen Closed Tab); a tab
/// opened for the user by something else (generated SQL, browse, an import, the
/// workspace restore) leaves focus where it was.
/// </summary>
public class NewTabFocusTests
{
    [Test]
    public async Task New_tab_chord_puts_typing_into_the_new_tabs_editor()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (editor, filter) = Parts(window);
            var first = vm.ActiveTab;
            var firstSql = first.Sql;
            FocusElsewhere(filter);

            Ui.Press(window, CommandId.NewTab);
            await Assert.That(vm.ActiveTab).IsNotEqualTo(first);
            await Assert.That(editor.TextArea.IsKeyboardFocusWithin).IsTrue();

            var before = vm.ActiveTab.Sql;
            Ui.Type(window, "abc");

            await Assert.That(vm.ActiveTab.Sql.Length).IsEqualTo(before.Length + 3);
            await Assert.That(vm.ActiveTab.Sql).Contains("abc");
            await Assert.That(filter.Text ?? "").DoesNotContain("abc");
            await Assert.That(first.Sql).IsEqualTo(firstSql);

            window.Close();
        });
    }

    /// <summary>
    /// The palette runs the command as it closes, with focus still in its search
    /// box: the editor has to win that, not the box being hidden.
    /// </summary>
    [Test]
    public async Task New_tab_from_the_palette_puts_typing_into_the_editor()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (editor, _) = Parts(window);
            var first = vm.ActiveTab;

            // Not awaited inside: the palette waits on the catalog's table list,
            // which the fixture data source never delivers (see ShellTests).
            Ui.Press(window, CommandId.CommandPalette);
            vm.CommandPalette.SearchText = "new query tab";
            Ui.Settle();
            await Assert.That(vm.CommandPalette.SelectedItem!.Title).Contains("New query tab", StringComparison.OrdinalIgnoreCase);

            Ui.Press(window, Key.Enter);
            await Assert.That(vm.CommandPalette.IsOpen).IsFalse();
            await Assert.That(vm.ActiveTab).IsNotEqualTo(first);
            await Assert.That(editor.TextArea.IsKeyboardFocusWithin).IsTrue();

            var before = vm.ActiveTab.Sql;
            Ui.Type(window, "abc");
            await Assert.That(vm.ActiveTab.Sql.Length).IsEqualTo(before.Length + 3);

            window.Close();
        });
    }

    [Test]
    public async Task Reopen_closed_tab_puts_typing_into_the_reopened_tab()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (editor, filter) = Parts(window);

            vm.AddTabCommand.Execute(null);
            var closing = vm.ActiveTab;
            closing.Sql = "SELECT 2;";
            Ui.Settle();
            vm.CloseTabCommand.Execute(closing);
            Ui.Settle();
            FocusElsewhere(filter);

            Ui.Press(window, CommandId.ReopenClosedTab);
            await Assert.That(vm.ActiveTab.Sql).IsEqualTo("SELECT 2;");
            await Assert.That(editor.TextArea.IsKeyboardFocusWithin).IsTrue();

            Ui.Type(window, "x");
            await Assert.That(vm.ActiveTab.Sql.Length).IsEqualTo("SELECT 2;".Length + 1);

            window.Close();
        });
    }

    /// <summary>
    /// The other half: a tab opened on the user's behalf must not take the keys
    /// from whatever they were typing in. Generated SQL stands in for the rest
    /// (browse, imports, the schema menu's templates): all of them go through
    /// <c>NewTab</c>, which raises nothing.
    /// </summary>
    [Test]
    public async Task A_tab_opened_for_the_user_leaves_focus_where_it_was()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (editor, filter) = Parts(window);
            FocusElsewhere(filter);

            var tab = vm.OpenGeneratedSql("grants", "GRANT SELECT ON t TO r;");
            Ui.Settle();

            await Assert.That(vm.ActiveTab).IsEqualTo(tab);
            await Assert.That(editor.TextArea.IsKeyboardFocusWithin).IsFalse();
            await Assert.That(filter.IsFocused).IsTrue();

            Ui.Type(window, "ord");
            await Assert.That(tab.Sql).IsEqualTo("GRANT SELECT ON t TO r;");
            await Assert.That(filter.Text).IsEqualTo("ord");

            window.Close();
        });
    }

    private static (TextEditor Editor, TextBox Filter) Parts(Window window)
    {
        var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
        var filter = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.PlaceholderText?.StartsWith("Filter schemas", StringComparison.Ordinal) == true);
        return (editor, filter);
    }

    // The sidebar's filter box: somewhere focus plausibly is, and not the editor,
    // so a pass can't come from focus that was already there.
    private static void FocusElsewhere(TextBox filter)
    {
        filter.Focus();
        Ui.Settle();
        if (!filter.IsFocused)
        {
            throw new InvalidOperationException("The sidebar filter box did not take focus.");
        }
    }
}
