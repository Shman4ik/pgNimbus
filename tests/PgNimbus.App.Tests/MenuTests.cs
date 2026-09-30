using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The macOS menu bar and the Edit-menu routing behind it, plus the context
/// menus' labels.
/// <para>
/// The menu bar is only ever installed on macOS, but it is built on every
/// platform, so what it holds is checked here wherever the suite runs. The
/// routing is the part that matters most: on macOS a menu item's key
/// equivalent is taken by AppKit before the key reaches the window, so once
/// the Edit menu carries Cmd+C, copy in a text box, the SQL editor or the
/// grid works only if <see cref="EditCommands"/> hands the verb to the right
/// control.
/// </para>
/// </summary>
public class MenuTests
{
    // --- Menu bar structure -------------------------------------------------

    [Test]
    public async Task Main_window_menu_bar_has_file_edit_query_view_and_window()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            var bar = ((MainWindow)window).CreateNativeMenuBar();

            await Assert.That(Join(Headers(bar))).IsEqualTo("File | Edit | Query | View | Window");
        });
    }

    [Test]
    public async Task File_menu_uses_the_short_mac_names()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            var file = Headers(Submenu(((MainWindow)window).CreateNativeMenuBar(), "File"));

            await Assert.That(file).Contains("Open…");
            await Assert.That(file).Contains("Save to Saved Queries…");
            await Assert.That(file).Contains("Save to File…");
            await Assert.That(file).DoesNotContain("Open .sql File…");
        });
    }

    [Test]
    public async Task Edit_menu_carries_the_standard_verbs_with_their_gestures()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            var edit = Submenu(((MainWindow)window).CreateNativeMenuBar(), "Edit");

            await Assert.That(Join(Headers(edit))).IsEqualTo("Undo | Redo | - | Cut | Copy | Paste | Select All | - | Find…");
            await Assert.That(Item(edit, "Copy").Gesture).IsEqualTo(EditCommands.GestureFor(EditCommand.Copy));
            await Assert.That(Item(edit, "Redo").Gesture).IsEqualTo(new KeyGesture(Key.Z, Hotkeys.Command | KeyModifiers.Shift));
            await Assert.That(Item(edit, "Find…").Gesture).IsEqualTo(CommandBindings.GestureFor(CommandId.Find));
        });
    }

    [Test]
    public async Task Window_menu_switches_tabs_with_the_catalog_gestures()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            var menu = Submenu(((MainWindow)window).CreateNativeMenuBar(), "Window");
            var headers = Headers(menu);

            await Assert.That(headers).Contains("Minimize");
            await Assert.That(headers).Contains("Zoom");
            await Assert.That(Item(menu, "Zoom").IsEnabled).IsTrue();
            await Assert.That(headers).Contains("Bring All to Front");
            await Assert.That(Item(menu, "Show Previous Tab").Gesture).IsEqualTo(CommandBindings.GestureFor(CommandId.PreviousTab));
            await Assert.That(Item(menu, "Show Next Tab").Gesture).IsEqualTo(CommandBindings.GestureFor(CommandId.NextTab));
        });
    }

    /// <summary>
    /// The connect window can't be maximized, so its Zoom is greyed out as AppKit
    /// greys it, instead of maximizing the window past its size cap.
    /// </summary>
    [Test]
    public async Task Connection_window_menu_greys_out_Zoom()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.ConnectionDialog();
            var menu = Submenu(((ConnectionDialog)window).CreateNativeMenuBar(), "Window");
            var zoom = Item(menu, "Zoom");

            await Assert.That(zoom.IsEnabled).IsFalse();
            Click(zoom);
            await Assert.That(window.WindowState).IsEqualTo(WindowState.Normal);
        });
    }

    [Test]
    public async Task Window_menu_tab_items_switch_the_active_tab()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.AddTabCommand.Execute(null);
            Ui.Settle();
            var second = vm.ActiveTab;

            var menu = Submenu(((MainWindow)window).CreateNativeMenuBar(), "Window");
            Click(Item(menu, "Show Previous Tab"));
            Ui.Settle();

            await Assert.That(vm.ActiveTab).IsNotSameReferenceAs(second);

            window.Close();
        });
    }

    [Test]
    public async Task View_menu_offers_appearance_as_three_radio_items()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            var view = Submenu(((MainWindow)window).CreateNativeMenuBar(), "View");

            await Assert.That(Headers(view)).DoesNotContain("Toggle Light/Dark Theme");
            var appearance = Submenu(view, "Appearance");
            await Assert.That(Join(Headers(appearance))).IsEqualTo("System | Light | Dark");
            await Assert.That(appearance.Items.OfType<NativeMenuItem>().All(i => i.ToggleType == MenuItemToggleType.Radio)).IsTrue();

            MacMenus.RefreshAppearance(appearance, "dark");
            await Assert.That(Join(appearance.Items.OfType<NativeMenuItem>().Where(i => i.IsChecked).Select(i => i.Header))).IsEqualTo("Dark");
        });
    }

    [Test]
    public async Task Connection_dialog_has_close_edit_and_window_basics()
    {
        await Ui.Run(async () =>
        {
            var dialog = (ConnectionDialog)Scenarios.ConnectionDialog();
            var bar = dialog.CreateNativeMenuBar();

            await Assert.That(Join(Headers(bar))).IsEqualTo("File | Edit | Window");
            await Assert.That(Item(Submenu(bar, "File"), "Close Window").Gesture).IsEqualTo(new KeyGesture(Key.W, Hotkeys.Command));
            await Assert.That(Join(Headers(Submenu(bar, "Edit")))).IsEqualTo("Undo | Redo | - | Cut | Copy | Paste | Select All");
            await Assert.That(Item(Submenu(bar, "Window"), "Minimize").Gesture).IsEqualTo(new KeyGesture(Key.M, Hotkeys.Command));
        });
    }

    // --- The app menu -------------------------------------------------------

    /// <summary>
    /// The two items Avalonia.Native gets wrong in the block it appends, built
    /// here the way its exporter builds them.
    /// </summary>
    [Test]
    public async Task App_menu_fix_rebinds_hide_others_and_names_quit()
    {
        await Ui.Run(async () =>
        {
            var menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("Hide pgNimbus") { Gesture = new KeyGesture(Key.H, KeyModifiers.Meta) },
                    new NativeMenuItem("Hide Others") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta | KeyModifiers.Alt) },
                    new NativeMenuItem("Show All"),
                    new NativeMenuItem("Quit") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) },
                },
            };

            var changed = MacAppMenu.FixStandardItems(menu, "pgNimbus");

            await Assert.That(changed).IsEqualTo(2);
            await Assert.That(Item(menu, "Hide Others").Gesture).IsEqualTo(new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt));
            await Assert.That(Item(menu, "Quit pgNimbus").Gesture).IsEqualTo(new KeyGesture(Key.Q, KeyModifiers.Meta));
            await Assert.That(Item(menu, "Hide pgNimbus").Gesture).IsEqualTo(new KeyGesture(Key.H, KeyModifiers.Meta));

            // Idempotent: a second pass finds nothing left to correct.
            await Assert.That(MacAppMenu.FixStandardItems(menu, "pgNimbus")).IsEqualTo(0);
        });
    }

    [Test]
    public async Task Settings_item_is_enabled_only_with_a_main_window()
    {
        await Ui.Run(async () =>
        {
            var appMenu = NativeMenu.GetMenu(Avalonia.Application.Current!);
            var settings = MacAppMenu.SettingsItem(appMenu);
            await Assert.That(settings).IsNotNull();

            MacAppMenu.UpdateSettingsItem(appMenu, hasMainWindow: false);
            await Assert.That(settings!.IsEnabled).IsFalse();

            MacAppMenu.UpdateSettingsItem(appMenu, hasMainWindow: true);
            await Assert.That(settings.IsEnabled).IsTrue();
        });
    }

    // --- Edit routing -------------------------------------------------------

    [Test]
    public async Task Edit_verbs_reach_a_focused_text_box()
    {
        await Ui.Run(async () =>
        {
            var source = new TextBox { Text = "hello" };
            var target = new TextBox();
            var window = new Window { Content = new StackPanel { Children = { source, target } }, Width = 300, Height = 200 };
            Ui.Show(window);

            source.Focus();
            Ui.Settle();
            await Assert.That(EditCommands.Execute(window, EditCommand.SelectAll)).IsTrue();
            await Assert.That(source.SelectedText).IsEqualTo("hello");

            EditCommands.Execute(window, EditCommand.Copy);
            Ui.Settle();
            target.Focus();
            Ui.Settle();
            EditCommands.Execute(window, EditCommand.Paste);
            await Assert.That(Ui.SettleUntil(() => target.Text == "hello")).IsTrue();

            EditCommands.Execute(window, EditCommand.Undo);
            Ui.Settle();
            await Assert.That(target.Text ?? "").IsEqualTo("");

            window.Close();
        });
    }

    [Test]
    public async Task Edit_verbs_reach_the_sql_editor()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            Ui.Show(window);
            var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
            editor.TextArea.Focus();
            Ui.Settle();

            await Assert.That(EditCommands.Execute(window, EditCommand.SelectAll)).IsTrue();
            await Assert.That(editor.SelectionLength).IsEqualTo(editor.Document.TextLength);

            var before = editor.Text;
            Ui.Type(window, "x");
            await Assert.That(editor.Text).IsEqualTo("x");
            EditCommands.Execute(window, EditCommand.Undo);
            Ui.Settle();
            await Assert.That(editor.Text).IsEqualTo(before);

            window.Close();
        });
    }

    [Test]
    public async Task Copy_in_the_results_grid_copies_its_rows()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
            grid.Focus();
            Ui.Settle();

            await Assert.That(EditCommands.Execute(window, EditCommand.SelectAll)).IsTrue();
            Ui.Settle();
            await Assert.That(grid.SelectedItems.Count).IsEqualTo(vm.ActiveTab.Rows.Count);

            await Assert.That(EditCommands.Execute(window, EditCommand.Copy)).IsTrue();
            Ui.Settle();
            var copied = await window.Clipboard!.TryGetTextAsync();

            // TSV of every selected row.
            await Assert.That(copied).IsNotNull();
            await Assert.That(copied!).Contains("\t");
            var lines = copied.TrimEnd('\n', '\r').Split('\n');
            await Assert.That(lines.Length).IsGreaterThanOrEqualTo(vm.ActiveTab.Rows.Count);

            window.Close();
        });
    }

    [Test]
    public async Task Find_opens_the_sql_editors_search_when_nothing_closer_takes_it()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            Ui.Show(window);
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
            grid.Focus();
            Ui.Settle();

            // Not browsing, so the grid has no filter to open and passes Find on.
            await Assert.That(EditCommands.Execute(window, EditCommand.Find)).IsTrue();
            Ui.Settle();

            var search = window.GetVisualDescendants().OfType<AvaloniaEdit.Search.SearchPanel>().FirstOrDefault(p => p.IsOpened);
            await Assert.That(search).IsNotNull();

            window.Close();
        });
    }

    [Test]
    public async Task An_edit_verb_nothing_can_take_is_a_no_op()
    {
        await Ui.Run(async () =>
        {
            var button = new Button { Content = "Go" };
            var window = new Window { Content = button, Width = 200, Height = 100 };
            Ui.Show(window);
            button.Focus();
            Ui.Settle();

            await Assert.That(EditCommands.Execute(window, EditCommand.Paste)).IsFalse();

            window.Close();
        });
    }

    // --- Context menus ------------------------------------------------------

    [Test]
    public async Task Table_menu_starts_with_browse_and_views_have_no_alter()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            Ui.Show(window);

            var table = ContextMenuFor(window, node => node is TableNode { Name: "customers" });
            await Assert.That(Join(VisibleHeaders(table))).IsEqualTo("Browse Rows | Copy Name | Source (DDL) | Alter Table…");

            var view = ContextMenuFor(window, node => node is TableNode { Name: "active_customers" });
            await Assert.That(Join(VisibleHeaders(view))).IsEqualTo("Browse Rows | Copy Name | Source (DDL)");

            window.Close();
        });
    }

    [Test]
    public async Task Schema_menu_is_in_title_case()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            Ui.Show(window);

            var schema = ContextMenuFor(window, node => node is SchemaNode { Name: "public" });
            await Assert.That(Join(VisibleHeaders(schema)))
                .IsEqualTo("New Table… | Copy Name | Refresh | Exclude from Autocomplete | Drop Schema… | Drop Schema (Cascade)…");

            window.Close();
        });
    }

    // --- Helpers ------------------------------------------------------------

    /// <summary>Order matters in a menu, so sequences are compared as one string.</summary>
    private static string Join(IEnumerable<string?> items) => string.Join(" | ", items);

    private static string?[] Headers(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToArray();

    private static NativeMenuItem Item(NativeMenu menu, string header) =>
        menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == header);

    private static NativeMenu Submenu(NativeMenu menu, string header) =>
        Item(menu, header).Menu ?? throw new InvalidOperationException($"{header} has no submenu.");

    /// <summary>Raises a native item's Click the way the exporter does.</summary>
    private static void Click(NativeMenuItem item) =>
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

    /// <summary>
    /// The context menu a tree row's template carries, opened and closed once so
    /// its items' bindings (the IsVisible ones) have been evaluated against the
    /// row: an unopened menu has no data context to bind to yet.
    /// </summary>
    private static ContextMenu ContextMenuFor(Window window, Func<object?, bool> node)
    {
        var panel = window.GetVisualDescendants().OfType<Panel>()
            .First(p => p.ContextMenu is not null && node(p.DataContext));
        var menu = panel.ContextMenu!;
        menu.Open(panel);
        Ui.Settle();
        menu.Close();
        Ui.Settle();
        return menu;
    }

    private static string?[] VisibleHeaders(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string).ToArray();
}
