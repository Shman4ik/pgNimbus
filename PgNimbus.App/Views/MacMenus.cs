using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace PgNimbus.App.Views;

/// <summary>
/// The macOS menu-bar pieces every window shares: the Edit menu and the Window
/// menu. <see cref="MainWindow"/> and <see cref="ConnectionDialog"/> both build
/// their bars from these, so the two cannot drift into two different Edit menus.
/// <para>
/// The builders run on every platform (the tests read the menus they return);
/// only the <c>NativeMenu.SetMenu</c> call in each window is macOS-only.
/// </para>
/// </summary>
public static class MacMenus
{
    /// <summary>A menu item that runs <paramref name="action"/> on click.</summary>
    /// <remarks>
    /// Click rather than <c>NativeMenuItem.Command</c>: the native exporter reads
    /// a command's CanExecute once, when it is assigned, so an item whose command
    /// is resolved later would stay greyed out for good (see CLAUDE.md, "macOS
    /// native menu bar").
    /// </remarks>
    public static NativeMenuItem Action(string header, Action action, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// The standard Edit menu: Undo, Redo, Cut, Copy, Paste, Select All and,
    /// where the window has something to search, Find. Every item goes through
    /// <see cref="EditCommands.Execute"/>, which hands it to whatever has focus;
    /// see there for why that routing is not optional. AppKit appends its own
    /// Emoji &amp; Symbols and Dictation items to a menu titled "Edit", which is
    /// expected.
    /// </summary>
    public static NativeMenu Edit(TopLevel window, bool includeFind)
    {
        var menu = new NativeMenu
        {
            Items =
            {
                EditItem(window, "Undo", EditCommand.Undo),
                EditItem(window, "Redo", EditCommand.Redo),
                new NativeMenuItemSeparator(),
                EditItem(window, "Cut", EditCommand.Cut),
                EditItem(window, "Copy", EditCommand.Copy),
                EditItem(window, "Paste", EditCommand.Paste),
                EditItem(window, "Select All", EditCommand.SelectAll),
            },
        };

        if (includeFind)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(EditItem(window, "Find…", EditCommand.Find));
        }

        return menu;
    }

    private static NativeMenuItem EditItem(TopLevel window, string header, EditCommand command) =>
        Action(header, () => EditCommands.Execute(window, command), EditCommands.GestureFor(command));

    /// <summary>The Appearance submenu's rows, in order: the persisted theme value and its label.</summary>
    private static readonly (string Value, string Header)[] Themes =
    [
        ("system", "System"),
        ("light", "Light"),
        ("dark", "Dark"),
    ];

    /// <summary>
    /// View → Appearance: System / Light / Dark as radio items, the current one
    /// checked. A choice goes through <see cref="App.SetTheme"/>, the same call
    /// the preferences page makes, so it is applied and persisted the same way.
    /// It replaced a "Toggle Light/Dark Theme" item, which could not get back to
    /// following the system once pressed. The checkmarks are read when the menu
    /// opens (see <see cref="RefreshAppearance"/>), not when it is built, so
    /// building a menu bar never reads the settings file.
    /// </summary>
    public static NativeMenu Appearance()
    {
        var menu = new NativeMenu();
        foreach (var (value, header) in Themes)
        {
            var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) =>
            {
                App.SetTheme(value);
                RefreshAppearance(menu, value);
            };
            menu.Items.Add(item);
        }

        menu.NeedsUpdate += (_, _) => RefreshAppearance(menu);
        return menu;
    }

    /// <summary>Checks the row of the persisted theme (or <paramref name="current"/> when given).</summary>
    public static void RefreshAppearance(NativeMenu menu, string? current = null)
    {
        current ??= App.LoadSettings().Theme?.ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system",
        };
        for (var i = 0; i < Themes.Length && i < menu.Items.Count; i++)
        {
            if (menu.Items[i] is NativeMenuItem item)
            {
                item.IsChecked = Themes[i].Value == current;
            }
        }
    }

    /// <summary>
    /// The Window menu: Minimize and Zoom, then <paramref name="windowItems"/>
    /// (the main window's tab switching), then Bring All to Front and the list of
    /// open windows, checked on the active one.
    /// <para>
    /// The list is drawn here rather than by AppKit because AppKit only keeps one
    /// for the menu registered as <c>NSApp.windowsMenu</c>, and Avalonia neither
    /// registers one nor exposes the call. It is rebuilt each time the menu
    /// opens, so it always matches the windows actually open.
    /// </para>
    /// </summary>
    private static NativeMenuItem ZoomItem(Window window)
    {
        var item = Action("Zoom", () =>
        {
            if (window.CanMaximize)
            {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
        });
        item.IsEnabled = window.CanMaximize;
        return item;
    }

    public static NativeMenu Window(Window window, IEnumerable<NativeMenuItemBase>? windowItems = null)
    {
        var menu = new NativeMenu
        {
            Items =
            {
                Action("Minimize", () => window.WindowState = WindowState.Minimized, new KeyGesture(Key.M, Hotkeys.Command)),
                // Greyed out, as AppKit does, for a window that can't be maximized
                // (the connect form): zooming it would sidestep its size cap.
                ZoomItem(window),
            },
        };

        if (windowItems is not null)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            foreach (var item in windowItems)
            {
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Action("Bring All to Front", BringAllToFront));

        var fixedCount = menu.Items.Count;
        menu.NeedsUpdate += (_, _) => RebuildWindowList(menu, fixedCount);
        RebuildWindowList(menu, fixedCount);
        return menu;
    }

    /// <summary>The app's open windows, in the order the Window menu lists them.</summary>
    public static IReadOnlyList<Window> OpenWindows() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.Where(w => w.IsVisible).ToList()
            : [];

    private static void RebuildWindowList(NativeMenu menu, int fixedCount)
    {
        while (menu.Items.Count > fixedCount)
        {
            menu.Items.RemoveAt(menu.Items.Count - 1);
        }

        var windows = OpenWindows();
        if (windows.Count == 0)
        {
            return;
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        foreach (var w in windows)
        {
            var item = new NativeMenuItem(string.IsNullOrWhiteSpace(w.Title) ? "pgNimbus" : w.Title)
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = w.IsActive,
            };
            item.Click += (_, _) => Raise(w);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Window → Bring All to Front: every open window is raised, and the one
    /// that was active ends up on top again, so the gesture reorders nothing
    /// between this app's own windows.
    /// </summary>
    public static void BringAllToFront()
    {
        var windows = OpenWindows();
        var active = windows.FirstOrDefault(w => w.IsActive);
        foreach (var w in windows)
        {
            if (!ReferenceEquals(w, active) && w.WindowState != WindowState.Minimized)
            {
                w.Activate();
            }
        }

        active?.Activate();
    }

    private static void Raise(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
