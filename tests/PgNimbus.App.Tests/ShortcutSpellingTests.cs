using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// How gestures are spelled and which ones answer, per hotkey scheme. The Mac
/// audit found every surface writing the Cmd scheme out as words ("Cmd+Enter /
/// F5", "Alt+Shift+F", keycaps reading "Cmd" "Shift" "Enter"), the empty grid
/// saying "Ctrl+Enter" on a Mac, and the next tab reachable only through a key
/// a Mac keyboard doesn't have.
///
/// Not in parallel: the scheme is process-wide (the preference is), so a test
/// that flips it must not share the session with one pressing chords.
/// </summary>
[NotInParallel]
public class ShortcutSpellingTests
{
    [Test]
    public async Task The_cmd_scheme_spells_every_surface_in_glyphs_and_the_ctrl_scheme_in_words()
    {
        await Ui.Run(async () =>
        {
            try
            {
                Nimbus.Ui.Hotkeys.Initialize("mac");
                var (window, vm) = Scenarios.Shell();
                Ui.Show(window);

                await Assert.That(SearchPillCaption(window)).IsEqualTo("⌘K");
                await Assert.That(RunHint(window)).IsEqualTo("Run a query with ⌘↩ or F5");
                await Assert.That(RunButtonTip(window)).EndsWith("(⌘↩)");
                await Assert.That(PaletteShortcut(vm, "Run query")).IsEqualTo("⌘↩ / F5");
                await Assert.That(PaletteShortcut(vm, "Rollback transaction")).IsEqualTo("⇧⌘⌫");
                await Assert.That(PaletteShortcut(vm, "Next tab")).IsEqualTo("⇧⌘] / ⌘⇟ / ⌃⇥");
                await Assert.That(SheetKeys("Format the statement under the cursor")).IsEqualTo("⇧ ⌘ F / ⌥ ⇧ F");

                // The preference is live: nothing needs a restart to re-spell.
                Nimbus.Ui.Hotkeys.Initialize("windows");
                Ui.Settle();

                await Assert.That(SearchPillCaption(window)).IsEqualTo("Ctrl+K");
                await Assert.That(RunHint(window)).IsEqualTo("Run a query with Ctrl+Enter or F5");
                await Assert.That(RunButtonTip(window)).EndsWith("(Ctrl+Enter)");
                await Assert.That(PaletteShortcut(vm, "Run query")).IsEqualTo("Ctrl+Enter / F5");
                await Assert.That(PaletteShortcut(vm, "Next tab")).IsEqualTo("Ctrl+PgDn / Ctrl+Tab");
                await Assert.That(SheetKeys("Format the statement under the cursor"))
                    .IsEqualTo("Ctrl Shift F / Alt Shift F");

                window.Close();
            }
            finally
            {
                Nimbus.Ui.Hotkeys.Initialize("auto");
            }
        });
    }

    [Test]
    public async Task Mac_tab_chords_switch_tabs()
    {
        await Ui.Run(async () =>
        {
            try
            {
                Nimbus.Ui.Hotkeys.Initialize("mac");
                var (window, vm) = Scenarios.Shell();
                Ui.Show(window);

                while (vm.Tabs.Count < 3)
                {
                    vm.AddTabCommand.Execute(null);
                }

                vm.ActiveTab = vm.Tabs[0];
                Ui.Settle();

                Ui.Press(window, Key.OemCloseBrackets, KeyModifiers.Meta | KeyModifiers.Shift);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[1]);

                Ui.Press(window, Key.Tab, KeyModifiers.Control);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[2]);

                Ui.Press(window, Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[1]);

                Ui.Press(window, Key.OemOpenBrackets, KeyModifiers.Meta | KeyModifiers.Shift);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[0]);

                // The cross-platform chord still works for a full-size keyboard.
                Ui.Press(window, Key.PageDown, KeyModifiers.Meta);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[1]);

                // And the catalog-driven press lands on the Mac's own chord.
                await Assert.That(CommandBindings.GestureFor(CommandId.NextTab))
                    .IsEqualTo(new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Meta | KeyModifiers.Shift));

                window.Close();
            }
            finally
            {
                Nimbus.Ui.Hotkeys.Initialize("auto");
            }
        });
    }

    [Test]
    public async Task Ctrl_tab_switches_tabs_on_the_ctrl_scheme_but_the_mac_brackets_do_not()
    {
        await Ui.Run(async () =>
        {
            try
            {
                Nimbus.Ui.Hotkeys.Initialize("windows");
                var (window, vm) = Scenarios.Shell();
                Ui.Show(window);

                vm.AddTabCommand.Execute(null);
                vm.ActiveTab = vm.Tabs[0];
                Ui.Settle();

                Ui.Press(window, Key.Tab, KeyModifiers.Control);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[1]);

                Ui.Press(window, Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[1]);

                window.Close();
            }
            finally
            {
                Nimbus.Ui.Hotkeys.Initialize("auto");
            }
        });
    }

    /// <summary>
    /// Ctrl+Tab is also Tab, which the editor indents with and the window's
    /// focus navigation moves on: the chord has to reach the tab strip from
    /// where focus actually is, not only from an unfocused window.
    /// </summary>
    [Test]
    [Arguments("mac")]
    [Arguments("windows")]
    public async Task Ctrl_tab_switches_tabs_from_the_editor_and_from_the_grid(string scheme)
    {
        await Ui.Run(async () =>
        {
            try
            {
                Nimbus.Ui.Hotkeys.Initialize(scheme);
                var (window, vm) = Scenarios.Shell();
                Ui.Show(window);

                vm.AddTabCommand.Execute(null);
                vm.ActiveTab = vm.Tabs[0];
                Ui.Settle();
                var sql = vm.Tabs[0].Sql;

                var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First(e => e.Name == "SqlEditor");
                editor.TextArea.Focus();
                Ui.Settle();
                await Assert.That(editor.TextArea.IsKeyboardFocusWithin).IsTrue();

                Ui.Press(window, Key.Tab, KeyModifiers.Control);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[1]);
                await Assert.That(vm.Tabs[0].Sql).IsEqualTo(sql);

                vm.ActiveTab = vm.Tabs[0];
                Ui.Settle();
                var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "ResultsGrid");
                grid.Focus();
                Ui.Settle();
                await Assert.That(grid.IsKeyboardFocusWithin).IsTrue();

                Ui.Press(window, Key.Tab, KeyModifiers.Control);
                await Assert.That(vm.ActiveTab).IsEqualTo(vm.Tabs[1]);

                window.Close();
            }
            finally
            {
                Nimbus.Ui.Hotkeys.Initialize("auto");
            }
        });
    }

    [Test]
    public async Task Cmd_question_mark_toggles_the_cheat_sheet_and_F1_still_does()
    {
        await Ui.Run(async () =>
        {
            try
            {
                Nimbus.Ui.Hotkeys.Initialize("mac");
                var (window, vm) = Scenarios.Shell();
                Ui.Show(window);

                Ui.Press(window, CommandId.ShortcutsWindow);
                await Assert.That(vm.IsShortcutsOpen).IsTrue();

                Ui.Press(window, Key.F1);
                await Assert.That(vm.IsShortcutsOpen).IsFalse();

                window.Close();
            }
            finally
            {
                Nimbus.Ui.Hotkeys.Initialize("auto");
            }
        });
    }

    [Test]
    public async Task Cmd_period_is_bound_to_cancel_on_the_cmd_scheme_only()
    {
        await Ui.Run(async () =>
        {
            try
            {
                var period = new KeyGesture(Key.OemPeriod, KeyModifiers.Meta);

                Nimbus.Ui.Hotkeys.Initialize("mac");
                var (window, _) = Scenarios.Shell();
                Ui.Show(window);
                await Assert.That(window.KeyBindings.Any(b => period.Equals(b.Gesture))).IsTrue();

                Nimbus.Ui.Hotkeys.Initialize("windows");
                Ui.Settle();
                await Assert.That(window.KeyBindings.Any(b => b.Gesture?.Key == Key.OemPeriod)).IsFalse();

                window.Close();
            }
            finally
            {
                Nimbus.Ui.Hotkeys.Initialize("auto");
            }
        });
    }

    private static string? SearchPillCaption(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PaletteSearchShortcut").Text;

    private static string RunHint(Window window)
    {
        var hint = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RunHintText");
        return string.Concat(hint.Inlines!.OfType<Run>().Select(r => r.Text));
    }

    // The toolbar's Run button: the one control whose tip names the Run command.
    private static string RunButtonTip(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => CommandTip.GetCommand(b) == CommandId.Run)
            .Select(b => ToolTip.GetTip(b) as string)
            .Single()!;

    private static string? PaletteShortcut(MainViewModel vm, string title)
    {
        // Fire and forget: the table half of the palette waits on a catalog
        // fetch that never answers against the fixture data source.
        _ = vm.OpenCommandPaletteAsync();
        Ui.Settle();
        vm.CommandPalette.SearchText = title;
        Ui.Settle();
        var shortcut = vm.CommandPalette.Results.First(i => i.Title == title).Shortcut;
        vm.CommandPalette.CloseCommand.Execute(null);
        Ui.Settle();
        return shortcut;
    }

    private static string SheetKeys(string action) =>
        string.Join(" ", new ShortcutsViewModel().Sections
            .SelectMany(s => s.Rows)
            .Single(r => r.Action == action)
            .Tokens.Select(t => t.Text));
}
