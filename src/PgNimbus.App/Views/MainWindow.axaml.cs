using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nimbus.Ui.Chrome;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Backup;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Localization;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

public partial class MainWindow : Window, IEditCommandTarget
{
    private MainViewModel? _viewModel;

    // Sidebar collapse (Ctrl+B): the width to restore to, and whether it's hidden.
    private const double SidebarMinWidth = 200;
    private GridLength _savedSidebarWidth = new(300);
    private bool _sidebarCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
        SetUpTitleBar();

        // Gestures use the platform/preference command modifier (Ctrl or Cmd),
        // so they're built in code, and rebuilt live when the scheme changes.
        BuildKeyBindings();
        Hotkeys.Changed += BuildKeyBindings;
        Closed += (_, _) => Hotkeys.Changed -= BuildKeyBindings;

        // The macOS menu bar is built once, in the language of the moment, so it is
        // built again when the language changes (everything else in the window
        // follows through its bindings).
        Localizer.WhenChanged(this, static window => window.BuildMacNativeMenu());

        // Closing this window ends its connection, and the SSH tunnel a running
        // backup or restore may be using, so what that would lose is asked
        // about first; and however the window went, what it had running on the
        // server is ended (MainViewModel.CloseConnectionAsync), with the app's
        // exit waiting a bounded moment for it (ConnectionCleanup).
        Closing += OnClosingAsync;
        Closed += (_, _) =>
        {
            if (_viewModel is { } vm)
            {
                ConnectionCleanup.Track(vm.CloseConnectionAsync());
            }
        };

        // ActualThemeVariant isn't final at construction time - re-resolve the
        // toggle glyph once the window opens, and again on any live theme switch,
        // however it originates. (The SQL editor's own highlighting theme is
        // re-resolved inside QueryEditorPanel.)
        Opened += (_, _) =>
        {
            UpdateThemeIcon();
            ApplyBackdrop();
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            UpdateThemeIcon();
            // ShellBackdropBrush is theme-split, so re-resolve on a theme flip.
            ApplyBackdrop();
        };
        // The backdrop material only renders while the window is active - swap
        // the shell base between translucent and opaque on focus changes.
        Activated += (_, _) => ApplyBackdrop();
        Deactivated += (_, _) => ApplyBackdrop();

        // Tab-strip navigation extras: the ‹/› arrows only appear when the
        // strip overflows, and the ▾ dropdown lists every open tab with
        // type-to-search for when scrolling would take too long.
        TabsList.Loaded += (_, _) => HookTabStripScroll();
        if (TabListButton.Flyout is Flyout tabFlyout)
        {
            tabFlyout.Opened += (_, _) => OpenTabList();
        }

        // The breadcrumb's database list is read fresh on every open.
        if (DatabaseSwitchButton.Flyout is Flyout databaseFlyout)
        {
            databaseFlyout.Opened += (_, _) => OpenDatabaseList();
        }

        // Drag a tab along the strip to reorder it. Pressed is tunneled so the
        // drag candidate is noted before the ListBoxItem handles selection;
        // moves/releases bubble up from the item that holds the pointer capture.
        TabsList.AddHandler(PointerPressedEvent, OnTabStripPointerPressed, RoutingStrategies.Tunnel);
        TabsList.PointerMoved += OnTabStripPointerMoved;
        TabsList.PointerReleased += (_, _) => EndTabDrag();
        TabsList.PointerCaptureLost += (_, _) => EndTabDrag();
        TabsList.ContextRequested += OnTabStripContextRequested;

        // The ☰ app menu's "Open recent" submenu reflects the list as of the
        // moment the menu opens, not app start.
        if (AppMenuButton.Flyout is MenuFlyout appMenu)
        {
            appMenu.Opened += (_, _) => RebuildRecentFilesMenu();
        }

        TabSearchBox.TextChanged += (_, _) => FilterTabList();
        TabSearchBox.KeyDown += OnTabSearchKeyDown;
        TabSearchList.Tapped += (_, e) =>
        {
            // Only a tap on an actual item activates; taps on the scrollbar
            // or empty space must not close the flyout.
            if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            {
                ActivateSelectedTabFromList();
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                Attach(vm);
            }
        };
    }

    /// <summary>
    /// Merges the command bar into the title bar: one row of chrome at the top
    /// instead of two (DESIGN.md rule 9). Shared with kubeNimbus — the platform
    /// rules this has to respect live on <see cref="NimbusWindowChrome"/>, and
    /// three of the four fail silently if got wrong.
    /// <para>
    /// This replaced a macOS-only version that hand-rolled the drag from
    /// <c>BeginMoveDrag</c> plus a <c>ClickCount == 2</c> zoom. That reproduced two
    /// of the four gestures a title bar owes the user and silently lost the
    /// right-click window menu and Win11 Snap Layouts — and on Windows it did not
    /// run at all, so the app carried a second bar there. The
    /// <c>ElementRole="TitleBar"</c> on the bar in XAML is what supplies all four
    /// now, from the OS.
    /// </para>
    /// <para>
    /// What stays macOS-specific is not chrome: the native menu bar
    /// (<see cref="BuildMacNativeMenu"/>) is the file-command home there, so the
    /// in-window ☰ menu would be a second copy of the same commands.
    /// </para>
    /// </summary>
    private void SetUpTitleBar()
    {
        // 16 keeps the bar's original horizontal breathing room; the caption
        // reserve is added on top of it, per platform, and taken back in full
        // screen where there are no caption buttons to reserve for.
        NimbusWindowChrome.Attach(this, CommandBar, RootLayout, inset: 16);

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        AppMenuButton.IsVisible = false;
        ConnectionBreadcrumb.Margin = new Thickness(0);
    }

    // KeyBinding.Command must be a live ICommand, but most targets hang off
    // ActiveTab, which changes on every tab switch — resolve at invoke time.
    private sealed class DelegatedCommand(Func<System.Windows.Input.ICommand?> resolve) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => resolve()?.CanExecute(parameter) ?? false;
        public void Execute(object? parameter) => resolve()?.Execute(parameter);
    }

    /// <summary>
    /// macOS-only: the real menu bar, wired to the same commands as the (hidden
    /// there) in-window ☰ menu, palette, and key bindings. Rebuilt from
    /// BuildKeyBindings so the displayed gestures track the live Ctrl/Cmd scheme;
    /// a fresh NativeMenu each time, so no handler ever double-subscribes. The
    /// app-level menu (About / Settings… / Hide / Quit) lives in App.axaml — this
    /// covers the window-scoped menus.
    /// </summary>
    private void BuildMacNativeMenu()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        NativeMenu.SetMenu(this, CreateNativeMenuBar());
    }

    /// <summary>
    /// The window's menu bar: File / Edit / Query / View / Window. Built on every
    /// platform so the tests can read it; only macOS ever shows it. Catalog
    /// commands go through <see cref="CommandItem(string, CommandId)"/>, which
    /// checks CanExecute at click time.
    /// </summary>
    public NativeMenu CreateNativeMenuBar()
    {
        var recentMenu = new NativeMenu();
        var fileMenu = new NativeMenu
        {
            Items =
            {
                CommandItem(Strings.Shell_MenuNewQueryTab, CommandId.NewTab),
                CommandItem(Strings.Shell_MenuOpen, CommandId.OpenFile),
                new NativeMenuItem(Strings.Shell_MenuOpenRecent) { Menu = recentMenu },
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Common_Save, CommandId.Save),
                CommandItem(Strings.Shell_MenuSaveAs, CommandId.SaveAs),
                CommandItem(Strings.Shell_MenuSaveToSavedQueries, CommandId.SaveQuery),
                CommandItem(Strings.Shell_MenuSaveToFile, CommandId.SaveFile),
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Shell_MenuCloseTab, CommandId.CloseTab),
                CommandItem(Strings.Shell_MenuReopenClosedTab, CommandId.ReopenClosedTab),
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Shell_MenuSwitchConnection, CommandId.SwitchConnection),
                CommandItem(Strings.Shell_MenuNewConnectionWindow, CommandId.NewWindow),
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Shell_MenuBackUpDatabase, CommandId.BackupDatabase),
                CommandItem(Strings.Shell_MenuRestoreBackup, CommandId.RestoreBackup),
            },
        };
        // Like the ☰ menu's submenu: reflects the list as of menu open.
        fileMenu.NeedsUpdate += (_, _) => RebuildMacRecentMenu(recentMenu);

        var queryMenu = new NativeMenu
        {
            Items =
            {
                CommandItem(Strings.Shell_MenuRun, CommandId.Run),
                CommandItem(Strings.Shell_MenuCancel, CommandId.Cancel),
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Shell_MenuExplain, CommandId.Explain),
                CommandItem(Strings.Shell_MenuExplainAnalyze, CommandId.ExplainAnalyze),
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Shell_MenuFormatSql, CommandId.FormatSql),
                CommandItem(Strings.Shell_MenuExpandStar, CommandId.ExpandStar),
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Shell_MenuBeginTransaction, CommandId.BeginTransaction),
                CommandItem(Strings.Shell_MenuCommitTransaction, CommandId.CommitTransaction),
                CommandItem(Strings.Shell_MenuRollbackTransaction, CommandId.RollbackTransaction),
                new NativeMenuItemSeparator(),
                CommandItem(Strings.Shell_MenuRefreshSchema, CommandId.RefreshSchema),
                CommandItem(Strings.Shell_MenuServerActivity, CommandId.ServerActivity),
                CommandItem(Strings.Shell_MenuDatabaseOverview, CommandId.DatabaseOverview),
                CommandItem(Strings.Shell_MenuSlowQueries, CommandId.SlowQueries),
                CommandItem(Strings.Shell_MenuQueryLab, CommandId.QueryLab),
                CommandItem(Strings.Shell_MenuNotifyMonitor, CommandId.NotifyMonitor),
                CommandItem(Strings.Shell_MenuRolesPermissions, CommandId.SecurityManager),
            },
        };

        // Finder-style Show/Hide phrasing, re-resolved every time the menu
        // opens. AppKit appends its own "Enter Full Screen" item to the menu
        // titled "View", so full screen isn't added here.
        var sidebarItem = MacMenus.Action(Strings.Shell_MenuHideSidebar, ToggleSidebar, CommandBindings.GestureFor(CommandId.ToggleSidebar));
        var appearanceMenu = MacMenus.Appearance();
        var viewMenu = new NativeMenu
        {
            Items =
            {
                MacMenus.Action(Strings.Shell_MenuCommandPalette, OpenCommandPalette, CommandBindings.GestureFor(CommandId.CommandPalette)),
                new NativeMenuItemSeparator(),
                sidebarItem,
                new NativeMenuItem(Strings.Shell_MenuAppearance) { Menu = appearanceMenu },
                new NativeMenuItemSeparator(),
                MacMenus.Action(Strings.Shell_MenuKeyboardShortcuts, () => _viewModel?.ShowShortcutsCommand.Execute(null), CommandBindings.GestureFor(CommandId.ShortcutsWindow)),
            },
        };
        viewMenu.NeedsUpdate += (_, _) =>
        {
            sidebarItem.Header = _sidebarCollapsed ? Strings.Shell_MenuShowSidebar : Strings.Shell_MenuHideSidebar;
            // The palette's theme toggle and the preferences page change the
            // theme too, so the checkmark is re-read here as well as when the
            // submenu itself opens.
            MacMenus.RefreshAppearance(appearanceMenu);
        };

        // Tab switching sits in Window, where Safari and Terminal keep it; the
        // gestures are the catalog's, so the menu can't advertise a chord the
        // window doesn't bind.
        var windowMenu = MacMenus.Window(this,
        [
            CommandItem(Strings.Shell_MenuPreviousTab, CommandId.PreviousTab),
            CommandItem(Strings.Shell_MenuNextTab, CommandId.NextTab),
        ]);

        // No Help menu on purpose: AppKit force-inserts a search field into
        // any menu named "Help" (it searches a help book this app doesn't
        // have), so its would-be items live in View (shortcuts) and the app
        // menu (GitHub link) instead.
        return new NativeMenu
        {
            Items =
            {
                new NativeMenuItem(Strings.Shell_MenuFile) { Menu = fileMenu },
                new NativeMenuItem(Strings.Shell_MenuEdit) { Menu = MacMenus.Edit(this, includeFind: true) },
                new NativeMenuItem(Strings.Shell_MenuQuery) { Menu = queryMenu },
                new NativeMenuItem(Strings.Shell_MenuView) { Menu = viewMenu },
                new NativeMenuItem(Strings.Shell_MenuWindow) { Menu = windowMenu },
            },
        };
    }

    /// <summary>
    /// A menu item for a catalog command: the gesture shown next to it and the
    /// command it runs both come from the catalog, so the menu can't advertise
    /// a shortcut the window doesn't actually bind.
    /// </summary>
    private NativeMenuItem CommandItem(string header, CommandId id) =>
        CommandItem(header, () => ResolveCommand(id), CommandBindings.GestureFor(id));

    private static NativeMenuItem CommandItem(string header, Func<System.Windows.Input.ICommand?> resolve, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        // Deliberately Click, not NativeMenuItem.Command: the native exporter
        // snapshots IsEnabled from CanExecute when the command is assigned —
        // here that's in the constructor, before the DataContext arrives, so
        // every item would export permanently grayed out (a wrapper that never
        // raises CanExecuteChanged is never re-read). Checking CanExecute at
        // click time is the same gate, evaluated when it matters.
        item.Click += (_, _) =>
        {
            var command = resolve();
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        };
        return item;
    }

    /// <summary>File → Open Recent, same contract as <see cref="RebuildRecentFilesMenu"/>.</summary>
    private void RebuildMacRecentMenu(NativeMenu menu)
    {
        menu.Items.Clear();
        if (_viewModel is not { RecentSqlFiles.Count: > 0 } viewModel)
        {
            menu.Items.Add(new NativeMenuItem(Strings.Shell_MenuNoRecentFiles) { IsEnabled = false });
            return;
        }

        foreach (var path in viewModel.RecentSqlFiles)
        {
            var item = new NativeMenuItem(Path.GetFileName(path));
            item.Click += (_, _) => _ = OpenRecentFileAsync(path);
            menu.Items.Add(item);
        }
    }

    private void BuildKeyBindings()
    {
        // The macOS menu bar shows the same gestures, so it rebuilds with them.
        BuildMacNativeMenu();

        KeyBindings.Clear();

        // Every window-level shortcut comes from the catalog — this loop is the
        // whole list. Gestures that a KeyBinding can't express (focus toggles,
        // keys a panel binds itself) are handled in OnKeyDown, still matching
        // the catalog's chord via CommandBindings.Matches.
        foreach (var descriptor in CommandCatalog.On(CommandSurface.WindowBinding))
        {
            var id = descriptor.Id;
            foreach (var gesture in CommandBindings.GesturesFor(id))
            {
                Add(gesture, () => ResolveCommand(id));
            }
        }

        // Ctrl/Cmd+1…9 jumps straight to a tab. Nine near-identical catalog
        // rows would just be palette noise, so the catalog documents the range
        // (CommandId.GoToTabByNumber) and the digits are bound here.
        for (var i = 0; i < 9; i++)
        {
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.D1 + i, Hotkeys.Command),
                Command = new DelegatedCommand(() => _viewModel?.GoToTabCommand),
                CommandParameter = i,
            });
        }

        // Shortcut-carrying tooltips are declared in XAML via CommandTip, which
        // reads the same catalog and re-renders itself on a scheme change.

        // The ☰ menu's shortcut captions: display-only gestures whose
        // Ctrl/Cmd side must match the bindings built just above.
        MenuNewTab.InputGesture = CommandBindings.GestureFor(CommandId.NewTab);
        MenuOpenFile.InputGesture = CommandBindings.GestureFor(CommandId.OpenFile);
        MenuSaveFile.InputGesture = CommandBindings.GestureFor(CommandId.Save);
        MenuSaveFileAs.InputGesture = CommandBindings.GestureFor(CommandId.SaveAs);
        MenuCloseTab.InputGesture = CommandBindings.GestureFor(CommandId.CloseTab);
        MenuReopenClosedTab.InputGesture = CommandBindings.GestureFor(CommandId.ReopenClosedTab);
        MenuPreferences.InputGesture = CommandBindings.GestureFor(CommandId.Preferences);
        MenuShortcuts.InputGesture = CommandBindings.GestureFor(CommandId.ShortcutsWindow);
        MenuSwitchConnection.InputGesture = CommandBindings.GestureFor(CommandId.SwitchConnection);
        MenuNewWindow.InputGesture = CommandBindings.GestureFor(CommandId.NewWindow);

        // The Explain button's two flyout items have a chord each, so they show
        // their own rather than the button's tooltip listing both.
        MenuExplain.InputGesture = CommandBindings.GestureFor(CommandId.Explain);
        MenuExplainAnalyze.InputGesture = CommandBindings.GestureFor(CommandId.ExplainAnalyze);

        // And the search pill's caption (the palette itself opens from
        // OnKeyDown, which reads the catalog's chord live).
        PaletteSearchShortcut.Text = CommandBindings.LabelFor(CommandId.CommandPalette);

        void Add(KeyGesture gesture, Func<System.Windows.Input.ICommand?> resolve) =>
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new DelegatedCommand(resolve) });
    }

    /// <summary>
    /// The catalog's command, bound to this window's view model. None while the
    /// cell inspector is open: it is modal over the window, and every chord used
    /// to reach through it to the tab behind. Ctrl+Enter in its editor re-ran the
    /// query and reloaded the grid under the cell being edited, and Ctrl+W closed
    /// the tab it was open over. The window's bindings see a key before the
    /// focused editor does, so this is also what lets CellValueView answer
    /// Ctrl+Enter with Save (measured: with the guard gone, the query runs and
    /// nothing is saved).
    /// </summary>
    private System.Windows.Input.ICommand? ResolveCommand(CommandId id) =>
        _viewModel is null || _viewModel.CellInspector.IsOpen ? null : CommandBindings.Resolve(id, _viewModel);

    /// <summary>
    /// The end of the Edit menu's routing walk (<see cref="EditCommands"/>): Find
    /// that nothing closer to focus took opens the SQL editor's search, which is
    /// what the Find chord does from anywhere in the window (see OnKeyDown).
    /// </summary>
    bool IEditCommandTarget.TryExecute(EditCommand command)
    {
        if (command != EditCommand.Find)
        {
            return false;
        }

        QueryEditor.OpenSearch(replaceMode: false);
        return true;
    }

    // F6 hops focus between the SQL editor and the results grid (the two
    // keyboard workspaces). Done in code because the target depends on where
    // focus currently is - a KeyBinding can't express a toggle.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_viewModel?.CellInspector.IsOpen == true)
        {
            if (e.Key == Key.Escape)
            {
                _viewModel.CellInspector.CloseCommand.Execute(null);
                e.Handled = true;
            }
            else if (CommandBindings.Matches(CommandId.Find, e))
            {
                // The value's own find bar, not the SQL editor's behind the overlay.
                ResultsPanel.OpenInspectorSearch();
                e.Handled = true;
            }

            // Nothing below may act on the window behind a modal overlay (see
            // ResolveCommand for the key bindings' half of this).
            return;
        }

        // Row details handles Esc itself while focus is in its form (revert
        // first, then close); this is the net for focus left elsewhere, so Esc
        // closes the overlay rather than reaching Cancel underneath it.
        if (e.Key == Key.Escape && _viewModel?.IsRowDetailOpen == true)
        {
            _viewModel.IsRowDetailOpen = false;
            e.Handled = true;
            return;
        }

        // These read their gestures from the catalog like every other shortcut;
        // they're handled here rather than as KeyBindings because each does
        // something a KeyBinding can't express (see the comment on each).
        if (CommandBindings.Matches(CommandId.CommandPalette, e) || CommandBindings.MatchesAlt(CommandId.CommandPalette, e))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (CommandBindings.Matches(CommandId.ShortcutsWindow, e))
        {
            // Toggles, so F1 closes the sheet it opened — the overlay has no
            // Esc-to-close of its own to fall back on being the only way out.
            _viewModel?.ShowShortcutsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (CommandBindings.Matches(CommandId.ToggleSidebar, e))
        {
            ToggleSidebar();
            e.Handled = true;
            return;
        }

        // Find / find & replace in the SQL editor. Handled here rather than a
        // KeyBinding so the Cmd scheme works even though SearchPanel.Install
        // also binds the physical Ctrl+F internally.
        if (CommandBindings.Matches(CommandId.Find, e))
        {
            QueryEditor.OpenSearch(replaceMode: false);
            e.Handled = true;
            return;
        }

        if (CommandBindings.Matches(CommandId.FindReplace, e))
        {
            QueryEditor.OpenSearch(replaceMode: true);
            e.Handled = true;
            return;
        }

        if (CommandBindings.Matches(CommandId.FocusSwap, e))
        {
            if (QueryEditor.IsEditorFocused)
            {
                ResultsPanel.FocusGrid();
            }
            else
            {
                QueryEditor.FocusEditor();
            }

            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // The production switch's confirm (ROADMAP T3): names the connection, says
    // the change is this window's only, and confirms in red, since what it
    // allows is exactly what the label exists to stop by accident.
    private Task<bool> ConfirmAllowWritesAsync()
    {
        var name = _viewModel is { } vm ? $"{vm.ConnectionName} › {vm.ConnectionDatabase}" : Strings.Shell_ThisConnection;
        var confirm = new ConfirmDialog(Strings.Shell_AllowWritesConfirm(name), Strings.Shell_AllowWritesButton);
        return confirm.ShowDialog<bool>(this);
    }

    // The Maintenance submenu's Truncate… and Drop… (and their palette rows):
    // the shared destructive confirm, worded by the view model.
    private Task<bool> ConfirmMaintenanceAsync(string message, string confirmLabel) =>
        new ConfirmDialog(message, confirmLabel).ShowDialog<bool>(this);

    // The Maintenance submenu's Rename…: the naming dialog Saved Queries'
    // Rename uses, with nothing to collide with (the server refuses a taken name).
    private async Task<string?> AskNewRelationNameAsync(string heading, string currentName)
    {
        var dialog = new SaveQueryDialog(() => heading, currentName, null, _ => null, SaveQueryKind.Relation,
            () => Strings.Shell_RenameDialogTitle, () => Strings.Common_Rename);
        return (await dialog.ShowDialog<SaveQueryResult?>(this))?.Name;
    }

    private void Attach(MainViewModel vm)
    {
        if (_viewModel is not null)
        {
            _viewModel.ThemeToggleRequested -= ToggleTheme;
            _viewModel.SwitchConnectionRequested -= SwitchConnection;
            _viewModel.SwitchDatabaseRequested -= ShowDatabaseList;
            _viewModel.NewWindowRequested -= OpenNewWindow;
            _viewModel.ImportPlanRequested -= ShowImportPlanDialog;
        _viewModel.ActivityRequested -= ShowActivityWindow;
            _viewModel.DatabaseOverviewRequested -= ShowDatabaseOverviewWindow;
            _viewModel.SlowQueriesRequested -= ShowSlowQueriesWindow;
            _viewModel.QueryLabRequested -= ShowQueryLabWindow;
            _viewModel.SavePlanRequested -= OnSavePlanRequested;
            _viewModel.NotifyMonitorRequested -= ShowNotifyMonitorWindow;
            _viewModel.SecurityRequested -= ShowSecurityWindow;
            _viewModel.BackupRequested -= ShowBackupWindow;
            _viewModel.RestoreRequested -= ShowRestoreWindow;
            _viewModel.SidebarToggleRequested -= ToggleSidebar;
            _viewModel.OpenFileRequested -= OnOpenFileRequested;
            _viewModel.SaveFileRequested -= OnSaveFileRequested;
            _viewModel.SaveQueryRequested -= OnSaveQueryRequested;
            _viewModel.OpenRecentFileRequested -= OnOpenRecentFileRequested;
            _viewModel.CloseWindowRequested -= Close;
        }

        _viewModel = vm;
        _viewModel.ConfirmAllowWritesRequested = ConfirmAllowWritesAsync;
        _viewModel.ConfirmMaintenanceRequested = ConfirmMaintenanceAsync;
        _viewModel.RenameRelationRequested = AskNewRelationNameAsync;
        _viewModel.ConfirmConnectionLossRequested = (message, label) => new ConfirmDialog(message, label).ShowDialog<bool>(this);
        // Palette actions that touch the window are handled here.
        _viewModel.ThemeToggleRequested += ToggleTheme;
        _viewModel.SwitchConnectionRequested += SwitchConnection;
        _viewModel.SwitchDatabaseRequested += ShowDatabaseList;
        _viewModel.NewWindowRequested += OpenNewWindow;
        // FormatSqlRequested / ExpandStarRequested / FindRequested are handled by
        // QueryEditorPanel, which subscribes to them off its own DataContext.
        _viewModel.ImportPlanRequested += ShowImportPlanDialog;
        _viewModel.ActivityRequested += ShowActivityWindow;
        _viewModel.DatabaseOverviewRequested += ShowDatabaseOverviewWindow;
        _viewModel.SlowQueriesRequested += ShowSlowQueriesWindow;
        _viewModel.QueryLabRequested += ShowQueryLabWindow;
        _viewModel.SavePlanRequested += OnSavePlanRequested;
        _viewModel.NotifyMonitorRequested += ShowNotifyMonitorWindow;
        _viewModel.SecurityRequested += ShowSecurityWindow;
        _viewModel.BackupRequested += ShowBackupWindow;
        _viewModel.RestoreRequested += ShowRestoreWindow;
        _viewModel.SidebarToggleRequested += ToggleSidebar;
        _viewModel.OpenFileRequested += OnOpenFileRequested;
        _viewModel.SaveFileRequested += OnSaveFileRequested;
        _viewModel.SaveQueryRequested += OnSaveQueryRequested;
        _viewModel.OpenRecentFileRequested += OnOpenRecentFileRequested;
        // macOS: Cmd+W on the only tab, once it is empty, closes the window.
        _viewModel.CloseWindowRequested += Close;

        // The results grid tracks the active tab itself, inside ResultsGridPanel
        // (off its own DataContext), same as the editor does in QueryEditorPanel.
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryViewModel tab })
        {
            _viewModel?.CloseTabCommand.Execute(tab);
        }
    }

    // --- Tab-strip drag reorder -------------------------------------------

    // The tab a press armed for dragging; the drag activates only once the
    // pointer travels past DragThreshold with the button down, so a plain
    // click stays a tab switch.
    private QueryViewModel? _tabDragCandidate;
    private Point _tabDragOrigin;
    private bool _tabDragActive;
    private const double DragThreshold = 4;

    private void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabsList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // A press on the tab's ✕ chip is a close click, never a drag; a press in
        // its open rename box is a caret placement (and dragging in it selects
        // text), so neither may arm the reorder.
        if (e.Source is Visual source
            && (source.FindAncestorOfType<Button>(includeSelf: true) is not null
                || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null))
        {
            return;
        }

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is QueryViewModel tab)
        {
            _tabDragCandidate = tab;
            _tabDragOrigin = e.GetPosition(TabsList);
            _tabDragActive = false;
        }
    }

    private void OnTabStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragCandidate is not { } tab || _viewModel is null)
        {
            return;
        }

        var position = e.GetPosition(TabsList);
        if (!_tabDragActive)
        {
            if (Math.Abs(position.X - _tabDragOrigin.X) < DragThreshold
                && Math.Abs(position.Y - _tabDragOrigin.Y) < DragThreshold)
            {
                return;
            }

            _tabDragActive = true;
            SetDraggedTabOpacity(0.55);
        }

        // Live reorder, browser-style: the dragged tab lands after every tab
        // whose center the pointer has passed. Centers are measured excluding
        // the dragged tab itself so a move doesn't immediately re-trigger.
        var targetIndex = 0;
        for (var i = 0; i < _viewModel.Tabs.Count; i++)
        {
            if (ReferenceEquals(_viewModel.Tabs[i], tab)
                || TabsList.ContainerFromIndex(i) is not { } container
                || container.TranslatePoint(default, TabsList) is not { } topLeft)
            {
                continue;
            }

            if (position.X > topLeft.X + container.Bounds.Width / 2)
            {
                targetIndex++;
            }
        }

        if (targetIndex != _viewModel.Tabs.IndexOf(tab))
        {
            _viewModel.MoveTab(tab, targetIndex);
            SetDraggedTabOpacity(0.55);
        }

        // Dragging against an overflowed strip's edge scrolls it, so a tab can
        // travel further than the visible span in one gesture.
        if (_tabsScrollViewer is { } scroller)
        {
            const double edge = 24;
            if (position.X < edge)
            {
                scroller.Offset = scroller.Offset.WithX(Math.Max(0, scroller.Offset.X - 8));
            }
            else if (position.X > TabsList.Bounds.Width - edge)
            {
                scroller.Offset = scroller.Offset.WithX(scroller.Offset.X + 8);
            }
        }
    }

    private void EndTabDrag()
    {
        if (_tabDragActive)
        {
            SetDraggedTabOpacity(1);
        }

        _tabDragCandidate = null;
        _tabDragActive = false;
    }

    // The faded "in flight" look rides on the item's container, which can be
    // re-realized across a collection Move — hence re-applied after each one.
    private void SetDraggedTabOpacity(double opacity)
    {
        if (_tabDragCandidate is { } tab && _viewModel is not null
            && _viewModel.Tabs.IndexOf(tab) is var index and >= 0
            && TabsList.ContainerFromIndex(index) is { } container)
        {
            container.Opacity = opacity;
        }
    }

    // --- Tab-strip context menu -------------------------------------------

    // Built lazily and re-shown, so the strip carries no extra always-visible
    // chrome: bulk tab management is a right-click (and a palette entry), never
    // a toolbar button. Rename plus the close family — the strip's own ✕, its ▾
    // finder and drag-reorder already cover the rest, and every item here is one
    // the tab bar can't express by pointing at a single tab.
    private MenuFlyout? _tabMenu;
    private MenuItem? _tabMenuSaveQuery;
    private MenuItem? _tabMenuRename;
    private MenuItem? _tabMenuClose;
    private MenuItem? _tabMenuCloseOthers;
    private MenuItem? _tabMenuCloseRight;

    private void OnTabStripContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } container
            || container.DataContext is not QueryViewModel tab)
        {
            return; // right-click on the strip's empty space: nothing to act on
        }

        // Right-click targets what it points at, the way VS and Notepad++ do,
        // so the menu's verbs read against the tab the user is looking at.
        _viewModel.ActiveTab = tab;

        _tabMenu ??= BuildTabMenu(_viewModel);

        // Each item's enabled state comes from its command's CanExecute against
        // this tab; assigning the parameter is what re-evaluates it.
        _tabMenuRename!.CommandParameter = tab;
        _tabMenuClose!.CommandParameter = tab;
        _tabMenuCloseOthers!.CommandParameter = tab;
        _tabMenuCloseRight!.CommandParameter = tab;

        _tabMenu.ShowAt(container, showAtPointer: true);
        e.Handled = true;
    }

    private MenuFlyout BuildTabMenu(MainViewModel viewModel)
    {
        // First item, above rename and the close family, because right-clicking
        // the tab is where users went looking for "save this query" and found
        // nothing — the only route was a name box buried in the sidebar. The
        // menu stays short (UI design rule 1): this earns its row by being the
        // reported gap, not by being one more thing that could go here.
        _tabMenuSaveQuery = new MenuItem
        {
            Command = viewModel.SaveQueryCommand,
        };
        _tabMenuRename = new MenuItem
        {
            Command = viewModel.RenameTabCommand,
        };
        _tabMenuClose = new MenuItem
        {
            Command = viewModel.CloseTabCommand,
            InputGesture = CommandBindings.GestureFor(CommandId.CloseTab),
        };
        _tabMenuCloseOthers = new MenuItem
        {
            Command = viewModel.CloseOtherTabsCommand,
        };
        _tabMenuCloseRight = new MenuItem
        {
            Command = viewModel.CloseTabsToTheRightCommand,
        };

        // The menu is built once and kept, so its labels are bound to the table and
        // follow a change of language.
        _tabMenuSaveQuery.Bind(MenuItem.HeaderProperty, Localizer.Observe(StringKey.Shell_TabMenuSaveQuery).ToBinding());
        _tabMenuRename.Bind(MenuItem.HeaderProperty, Localizer.Observe(StringKey.Shell_TabMenuRename).ToBinding());
        _tabMenuClose.Bind(MenuItem.HeaderProperty, Localizer.Observe(StringKey.Common_Close).ToBinding());
        _tabMenuCloseOthers.Bind(MenuItem.HeaderProperty, Localizer.Observe(StringKey.Shell_TabMenuCloseOthers).ToBinding());
        _tabMenuCloseRight.Bind(MenuItem.HeaderProperty, Localizer.Observe(StringKey.Shell_TabMenuCloseToTheRight).ToBinding());

        return new MenuFlyout
        {
            Items =
            {
                _tabMenuSaveQuery, new Separator(),
                _tabMenuRename, new Separator(),
                _tabMenuClose, _tabMenuCloseOthers, _tabMenuCloseRight,
            },
        };
    }

    // --- Inline tab rename ------------------------------------------------

    // A rename box that nobody can type into is the whole feature missing, and
    // two things conspire to produce exactly that:
    //
    // 1. IsVisible="False" keeps a control in the visual tree, so attachment
    //    fires once when the tab's container is realized — not when rename
    //    starts. Focus therefore follows the *visibility flip*, which is what
    //    this subscription is for; the attach handler only wires it up (and
    //    covers the case of a container realized already renaming).
    // 2. Rename starts from the tab strip's context flyout, and a closing
    //    flyout restores focus to whatever held it before it opened (the SQL
    //    editor). Focusing in the same frame loses that race — the box appears,
    //    and the name the user types lands in their query instead. One frame
    //    later the flyout is gone and the focus sticks.
    private void OnTabRenameBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        box.PropertyChanged -= OnTabRenameBoxPropertyChanged;
        box.PropertyChanged += OnTabRenameBoxPropertyChanged;

        if (box.IsVisible)
        {
            FocusTabRenameBox(box);
        }
    }

    private void OnTabRenameBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && sender is TextBox { IsVisible: true } box)
        {
            FocusTabRenameBox(box);
        }
    }

    private static void FocusTabRenameBox(TextBox box) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                // The tab may have left rename mode again before this ran.
                if (box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
            },
            DispatcherPriority.Input);

    private void OnTabRenameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: QueryViewModel tab })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            tab.CommitRename();
            e.Handled = true;
            QueryEditor.FocusEditor();
        }
        else if (e.Key == Key.Escape)
        {
            tab.CancelRename();
            e.Handled = true;
            QueryEditor.FocusEditor();
        }
    }

    // Clicking away commits, the way an in-place rename does everywhere else
    // (Explorer, VS Code's explorer). Escape has already left rename mode by
    // the time focus moves, so it can't be undone by this.
    private void OnTabRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: QueryViewModel tab })
        {
            tab.CommitRename();
        }
    }

    // --- Tab-strip navigation extras -------------------------------------

    private ScrollViewer? _tabsScrollViewer;
    private const double TabScrollStep = 160;

    private void HookTabStripScroll()
    {
        if (_tabsScrollViewer is not null)
        {
            return;
        }

        _tabsScrollViewer = TabsList.FindDescendantOfType<ScrollViewer>();
        if (_tabsScrollViewer is null)
        {
            return;
        }

        // ScrollChanged also fires on extent changes (tabs opened/closed/
        // retitled), so one subscription keeps the arrows in sync with both
        // scrolling and the tab set itself.
        _tabsScrollViewer.ScrollChanged += (_, _) => UpdateTabScrollArrows();
        UpdateTabScrollArrows();
    }

    private void UpdateTabScrollArrows()
    {
        if (_tabsScrollViewer is not { } viewer)
        {
            return;
        }

        var overflows = viewer.Extent.Width > viewer.Viewport.Width + 1;
        TabScrollLeftButton.IsVisible = overflows;
        TabScrollRightButton.IsVisible = overflows;
        if (!overflows)
        {
            return;
        }

        TabScrollLeftButton.IsEnabled = viewer.Offset.X > 1;
        TabScrollRightButton.IsEnabled = viewer.Offset.X < viewer.Extent.Width - viewer.Viewport.Width - 1;
    }

    private void OnTabScrollLeft(object? sender, RoutedEventArgs e)
    {
        if (_tabsScrollViewer is { } viewer)
        {
            viewer.Offset = viewer.Offset.WithX(Math.Max(0, viewer.Offset.X - TabScrollStep));
        }
    }

    private void OnTabScrollRight(object? sender, RoutedEventArgs e)
    {
        if (_tabsScrollViewer is { } viewer)
        {
            viewer.Offset = viewer.Offset.WithX(Math.Min(viewer.Extent.Width - viewer.Viewport.Width, viewer.Offset.X + TabScrollStep));
        }
    }

    // The palette's "Switch database…": the same list, opened from the breadcrumb
    // button it hangs off (posted, so the closing palette can't take focus back).
    private void ShowDatabaseList() =>
        Dispatcher.UIThread.Post(() => DatabaseSwitchButton.Flyout?.ShowAt(DatabaseSwitchButton));

    private DatabaseSwitcherViewModel? _databaseSwitcher;

    private void OpenDatabaseList()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_databaseSwitcher is not null)
        {
            _databaseSwitcher.CloseRequested -= CloseDatabaseList;
        }

        _databaseSwitcher = _viewModel.CreateDatabaseSwitcher();
        _databaseSwitcher.CloseRequested += CloseDatabaseList;
        DatabaseSwitcher.DataContext = _databaseSwitcher;
        _ = _databaseSwitcher.LoadAsync();
        DatabaseSwitcher.FocusFilter();
    }

    private void CloseDatabaseList() => DatabaseSwitchButton.Flyout?.Hide();

    private void OpenTabList()
    {
        TabSearchBox.Text = string.Empty;
        FilterTabList();
        // Focus after the flyout finishes opening, or it reclaims focus itself.
        Dispatcher.UIThread.Post(() => TabSearchBox.Focus());
    }

    private void FilterTabList()
    {
        if (_viewModel is null)
        {
            return;
        }

        var query = TabSearchBox.Text ?? string.Empty;
        var matches = _viewModel.Tabs
            .Where(t => t.TabTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        TabSearchList.ItemsSource = matches;
        // The active tab keeps the highlight while it matches; otherwise the
        // best (first) match takes it so Enter always has a target.
        TabSearchList.SelectedItem = matches.FirstOrDefault(t => ReferenceEquals(t, _viewModel.ActiveTab)) ?? matches.FirstOrDefault();
    }

    private void OnTabSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var count = TabSearchList.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                TabSearchList.SelectedIndex = Math.Min(TabSearchList.SelectedIndex + 1, count - 1);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                TabSearchList.SelectedIndex = Math.Max(TabSearchList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                ActivateSelectedTabFromList();
                e.Handled = true;
                break;
        }
    }

    private void ActivateSelectedTabFromList()
    {
        if (_viewModel is not null && TabSearchList.SelectedItem is QueryViewModel tab)
        {
            _viewModel.ActiveTab = tab;
        }

        TabListButton.Flyout?.Hide();
    }

    private void OnToggleSidebarClick(object? sender, RoutedEventArgs e) => ToggleSidebar();

    // Fully hides the schema/queries/notify sidebar (and its splitter) to give the
    // editor and results the whole width, or restores it. The last manual width is
    // remembered so re-showing returns to where the user had dragged it. Collapsing
    // also drops the column's 200px floor (it's the manual-resize guard, not a
    // collapse guard) so the column can actually reach zero.
    private void ToggleSidebar()
    {
        var column = ContentGrid.ColumnDefinitions[0];
        _sidebarCollapsed = !_sidebarCollapsed;

        if (_sidebarCollapsed)
        {
            _savedSidebarWidth = column.Width is { IsAbsolute: true, Value: > 0 } w ? w : new GridLength(300);
            column.MinWidth = 0;
            column.Width = new GridLength(0);
            SidebarTabs.IsVisible = false;
            SidebarSplitter.IsVisible = false;
        }
        else
        {
            column.MinWidth = SidebarMinWidth;
            column.Width = _savedSidebarWidth;
            SidebarTabs.IsVisible = true;
            SidebarSplitter.IsVisible = true;
        }
    }

    // Explicit light/dark switch in the title bar, so the app isn't forced to
    // follow the OS. Setting the application variant flips ActualThemeVariant on
    // the window, which the ActualThemeVariantChanged handler above picks up to
    // repaint the SQL palette and swap this button's glyph.
    private void OnToggleThemeClick(object? sender, RoutedEventArgs e) => ToggleTheme();

    private void ToggleTheme()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        // Remember the choice so the next launch doesn't snap back to the OS default.
        App.PersistTheme(app.RequestedThemeVariant);
    }

    // Show the glyph for where a click will take you: a sun while dark (click
    // to go light), a moon while light (click to go dark).
    private void UpdateThemeIcon()
    {
        var key = ActualThemeVariant == ThemeVariant.Dark ? "WeatherSunnyIconGeometry" : "WeatherNightIconGeometry";
        if (this.TryFindResource(key, out var geometry) && geometry is Geometry data)
        {
            ThemeIcon.Data = data;
        }
    }

    // Windows 11 Mica backdrop. When the platform honors a translucent backdrop
    // (Mica, or Acrylic as the Windows 10 fallback), the two-tone shell's base
    // becomes ShellBackdropBrush so the backdrop shows through it; the content
    // pane stays opaque. Anywhere the hint can't be honored (Linux, macOS, older
    // Windows, transparency disabled), ActualTransparencyLevel is None and the
    // base keeps its opaque chrome tone. The IsActive check matters on Windows:
    // DWM drops the backdrop material while the window is inactive, leaving the
    // mostly-transparent tint sitting on black - so an unfocused window must
    // fall back to the opaque tone too (same fallback WinUI apps apply).
    private void ApplyBackdrop()
    {
        var backdropActive = (ActualTransparencyLevel == WindowTransparencyLevel.Mica
            || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
            && IsActive;

        var key = backdropActive
            ? "ShellBackdropBrush"
            : "SystemControlBackgroundChromeMediumLowBrush";

        // Must resolve against the actual theme: ShellBackdropBrush lives only in
        // Light/Dark theme dictionaries (no Default), so the theme-less overload
        // silently misses it and the swap never happens.
        if (this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush)
        {
            ShellBase.Background = brush;
        }
    }

    private void OnSwitchConnectionClick(object? sender, RoutedEventArgs e) => SwitchConnection();

    // Reopens the connection dialog so a different profile (or an ad-hoc
    // connection) can be chosen without restarting the app. This window stays
    // usable until the new connection succeeds; only then does the dialog's
    // Connected handler close it (tearing down its notify listener/tunnel via
    // the Closed hook in App.BuildMainWindow). Cancelling the dialog leaves
    // everything as it was.
    private async void SwitchConnection()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        // Asked before the dialog opens, like the database switch asks before it
        // connects: the answer decides whether to pick a connection at all, and
        // a question popping up over the new window once it is connected would
        // come after the fact. The close that follows a connect doesn't ask again.
        if (_viewModel?.DescribeConnectionLosses() is { } losses
            && !await new ConfirmDialog(
                Strings.Shell_SwitchConnectionConfirm(losses), Strings.Shell_SwitchConnectionButton)
                .ShowDialog<bool>(this))
        {
            return;
        }

        var dialog = App.BuildConnectionDialog(desktop, previousWindow: this);
        dialog.Show(this);
    }

    // Opens the connection dialog additively: this window stays connected and
    // open, and a successful connect just adds another window rather than
    // replacing anything (App.BuildConnectionDialog, replaceMainWindow: false).
    private void OpenNewWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var dialog = App.BuildConnectionDialog(desktop, replaceMainWindow: false);
        dialog.Show();   // free-standing, NOT Show(this) — the dialog must not be owned by / pinned above the current window
    }

    private void ShowImportPlanDialog() => _ = ShowImportPlanDialogAsync();

    // Opens the paste-a-plan modal; on a successful parse the plan opens in a new tab
    // (MainViewModel.OpenImportedPlan) with no DB round-trip.
    private async Task ShowImportPlanDialogAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialog = new ImportPlanDialog();
        if (await dialog.ShowDialog<ImportedPlan?>(this) is { } imported)
        {
            _viewModel.OpenImportedPlan(imported);
        }
    }

    private ActivityWindow? _activityWindow;

    private void OnShowActivityClick(object? sender, RoutedEventArgs e) => ShowActivityWindow();

    // One live instance: reopening focuses it instead of stacking pollers.
    private void ShowActivityWindow()
    {
        if (_activityWindow is not null)
        {
            _activityWindow.Activate();
            return;
        }

        _activityWindow = new ActivityWindow { DataContext = _viewModel?.Activity };
        _activityWindow.Closed += (_, _) => _activityWindow = null;
        _activityWindow.Show(this);
    }

    private DatabaseOverviewWindow? _databaseOverviewWindow;

    // One live instance, like the activity window: reopening focuses it.
    private void ShowDatabaseOverviewWindow()
    {
        if (_databaseOverviewWindow is not null)
        {
            _databaseOverviewWindow.Activate();
            return;
        }

        _databaseOverviewWindow = new DatabaseOverviewWindow { DataContext = _viewModel?.DatabaseOverview };
        _databaseOverviewWindow.Closed += (_, _) => _databaseOverviewWindow = null;
        _databaseOverviewWindow.Show(this);
    }

    private SlowQueriesWindow? _slowQueriesWindow;

    // One live instance, like the other reference windows. It matters more here:
    // the window's first read is its interval baseline, and a second window
    // would quietly count from a different one.
    private void ShowSlowQueriesWindow()
    {
        if (_slowQueriesWindow is not null)
        {
            _slowQueriesWindow.Activate();
            return;
        }

        _slowQueriesWindow = new SlowQueriesWindow { DataContext = _viewModel?.SlowQueries };
        _slowQueriesWindow.Closed += (_, _) => _slowQueriesWindow = null;
        _slowQueriesWindow.Show(this);
    }

    private QueryLabWindow? _queryLabWindow;

    // One live instance, like the reference windows: it is read beside the query
    // whose plan it compares, and a second one would only show the same plans.
    private void ShowQueryLabWindow()
    {
        if (_queryLabWindow is not null)
        {
            _queryLabWindow.Activate();
            return;
        }

        _queryLabWindow = new QueryLabWindow { DataContext = _viewModel?.QueryLab };
        _queryLabWindow.Closed += (_, _) => _queryLabWindow = null;
        _queryLabWindow.Show(this);
    }

    private void OnSavePlanRequested(PlanCapture capture) => _ = SavePlanAsync(capture);

    /// <summary>
    /// Names the active tab's plan and keeps it in Query Lab. A tab showing a plan
    /// that is already saved suggests that plan's name, and saving under it updates
    /// it rather than adding a copy, as Save to Saved Queries does.
    /// </summary>
    private async Task SavePlanAsync(PlanCapture capture)
    {
        if (_viewModel is null)
        {
            return;
        }

        var lab = _viewModel.QueryLab;
        var tab = _viewModel.ActiveTab;
        await lab.ReloadAsync();
        var current = capture.Saved is { } saved && lab.FindByName(saved.Name)?.Id == saved.Id ? saved : null;
        var dialog = new SaveQueryDialog(
            () => Strings.Shell_SavePlanHeading,
            current?.Name ?? SaveQueryNames.Suggest(tab.TabTitle, name => lab.FindByName(name) is not null),
            current?.Id,
            name => lab.FindByName(name) is { } plan ? (plan.Id, plan.Name) : null,
            SaveQueryKind.SavedPlan,
            () => Strings.Shell_SavePlanTitle);

        if (await dialog.ShowDialog<SaveQueryResult?>(this) is not { } result)
        {
            return;
        }

        var entry = await lab.SaveAsync(capture, result.Name, result.OverwriteId);
        if (entry is null)
        {
            tab.Status = lab.Status ?? Strings.Shell_PlanNotSaved;
            tab.HasError = true;
            return;
        }

        // A second save of this plan updates the entry it now is.
        if (ReferenceEquals(tab.PlanCapture, capture))
        {
            tab.PlanCapture = capture with { Saved = entry };
        }

        tab.Status = Strings.Shell_PlanSaved(entry.Name);
        tab.HasError = false;
    }

    private NotifyMonitorWindow? _notifyMonitorWindow;

    // One live instance, like the other reference windows — and load-bearing
    // here rather than merely tidy: a second window would mean a second
    // listener holding its own connection open on the same channels, and every
    // NOTIFY would appear to arrive twice.
    private void ShowNotifyMonitorWindow()
    {
        if (_notifyMonitorWindow is not null)
        {
            _notifyMonitorWindow.Activate();
            return;
        }

        _notifyMonitorWindow = new NotifyMonitorWindow { DataContext = _viewModel?.NotifyMonitor };
        _notifyMonitorWindow.Closed += (_, _) => _notifyMonitorWindow = null;
        _notifyMonitorWindow.Show(this);
    }

    private Security.SecurityWindow? _securityWindow;

    // One live instance, like the other two reference windows. Deliberately a
    // window and not an overlay: it is read beside the editor while a grant is
    // being fixed, and the scripts it generates land in that editor's tabs.
    private void ShowSecurityWindow()
    {
        if (_securityWindow is not null)
        {
            _securityWindow.Activate();
            return;
        }

        _securityWindow = new Security.SecurityWindow { DataContext = _viewModel?.Security };
        _securityWindow.Closed += (_, _) => _securityWindow = null;
        _securityWindow.Show(this);
    }

    private BackupWindow? _backupWindow;
    private bool _closingConfirmed;

    /// <summary>
    /// Opens the backup window for <paramref name="scope"/>, one per main
    /// window. A window that is running a backup is only brought forward (the
    /// one running is the one that matters); an idle one is pointed at the new
    /// scope, so right-clicking another schema backs that one up.
    /// </summary>
    private void ShowBackupWindow(BackupScope scope)
    {
        if (_viewModel?.Backups is not { } backups)
        {
            return;
        }

        if (_backupWindow is { DataContext: BackupViewModel { IsRunning: true } })
        {
            _backupWindow.Activate();
            return;
        }

        var model = new BackupViewModel(
            backups,
            scope,
            ConnectionLabel(_viewModel),
            App.LoadSettings().LastBackupFolder,
            App.PersistLastBackupFolder);

        if (_backupWindow is not null)
        {
            (_backupWindow.DataContext as BackupViewModel)?.Dispose();
            _backupWindow.DataContext = model;
            _ = model.LoadAsync();
            _backupWindow.Activate();
            return;
        }

        _backupWindow = new BackupWindow { DataContext = model };
        _backupWindow.Closed += (_, _) => _backupWindow = null;
        _backupWindow.Show(this);
    }

    /// <summary>"prod-eu · app@db.example.com:5432/shop": the profile, and where it points.</summary>
    private static string ConnectionLabel(MainViewModel model)
    {
        var endpoint = model.ConnectionEndpoint.Split('\n')[0];
        return string.Equals(model.ConnectionName, model.ConnectionHost, StringComparison.Ordinal)
            ? endpoint
            : $"{model.ConnectionName} · {endpoint}";
    }

    private RestoreWindow? _restoreWindow;

    /// <summary>
    /// Opens the restore window, one per main window: a second request brings
    /// the open one forward, whatever it is doing.
    /// </summary>
    private void ShowRestoreWindow()
    {
        if (_viewModel?.Restores is not { } restores)
        {
            return;
        }

        if (_restoreWindow is not null)
        {
            _restoreWindow.Activate();
            return;
        }

        var model = new RestoreViewModel(restores, ConnectionLabel(_viewModel), openDatabase: _viewModel.OpenDatabaseInNewWindow);
        // A restore into this database changed what the schema tree shows.
        model.CurrentDatabaseChanged += () => _ = _viewModel.RefreshSchemaCommand.ExecuteAsync(null);
        _restoreWindow = new RestoreWindow { DataContext = model };
        _restoreWindow.Closed += (_, _) => _restoreWindow = null;
        _restoreWindow.Show(this);
    }

    // Set when a database switch or Switch Connection already asked about this
    // window's losses, so the close that follows doesn't ask the same again.
    private bool _lossesConfirmed;

    // True while the close question is on screen: a second close (a quit that
    // the first answer resumed, a second Alt+F4) waits for that answer.
    private bool _askingToClose;

    /// <summary>
    /// Closes the window without asking about its connection's losses, for a
    /// caller that already asked (the database switch, Switch Connection). A
    /// running backup or restore is still asked about: neither caller did.
    /// </summary>
    public void CloseConfirmed()
    {
        _lossesConfirmed = true;
        Close();
    }

    /// <summary>
    /// What a close would lose, as the one question to ask, or null to close
    /// at once. A running backup or restore comes first (it has its own Stop
    /// button label); the connection's losses (staged changes, the open
    /// transaction, running queries) follow in the same sentence, so the two
    /// are never asked one after the other.
    /// </summary>
    public static (string Message, string Label)? CloseQuestion(bool backupRunning, bool restoreRunning, string? losses)
    {
        if (restoreRunning)
        {
            return (losses is null ? Strings.Shell_CloseRestoreRunning : Strings.Shell_CloseRestoreRunningLosses(losses), Strings.Shell_StopRestore);
        }

        if (backupRunning)
        {
            return (losses is null ? Strings.Shell_CloseBackupRunning : Strings.Shell_CloseBackupRunningLosses(losses), Strings.Shell_StopBackup);
        }

        return losses is null ? null : (Strings.Shell_CloseWindowQuestion(losses), Strings.Shell_CloseWindowButton);
    }

    private async void OnClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        // A logout or OS shutdown can't wait for an answer: the window closes and
        // its Closed path ends the server-side work as far as time allows.
        if (_closingConfirmed || e.CloseReason == WindowCloseReason.OSShutdown)
        {
            return;
        }

        if (_askingToClose)
        {
            e.Cancel = true;
            return;
        }

        var backup = _backupWindow?.DataContext as BackupViewModel is { IsRunning: true } b ? b : null;
        var restore = _restoreWindow?.DataContext as RestoreViewModel is { IsRunning: true } r ? r : null;
        var losses = _lossesConfirmed ? null : _viewModel?.DescribeConnectionLosses();
        if (CloseQuestion(backup is not null, restore is not null, losses) is not { } question)
        {
            return;
        }

        // Cancelled before the first await, so the close (and a quit that asked
        // every window to close) stops here until the answer is in.
        e.Cancel = true;
        bool confirmed;
        _askingToClose = true;
        try
        {
            confirmed = await new ConfirmDialog(question.Message, question.Label).ShowDialog<bool>(this);
        }
        finally
        {
            _askingToClose = false;
        }

        if (!confirmed)
        {
            return;
        }

        if (backup is not null)
        {
            await backup.StopAndWaitAsync();
        }

        if (restore is not null)
        {
            await restore.StopAndWaitAsync();
        }

        _closingConfirmed = true;
        Close();

        // The close was part of quitting the app (macOS ⌘Q): carry the quit on,
        // so the next window is asked in turn and a clean set of windows quits.
        if (e.CloseReason == WindowCloseReason.ApplicationShutdown
            && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.TryShutdown();
        }
    }

    // The Explain toolbar button is a single slot with a flyout (minimalist rule);
    // menu items route to the active tab's commands the same way the results
    // grid's export does.
    private void OnExplainClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.ActiveTab is { } query && query.ExplainCommand.CanExecute(null))
        {
            query.ExplainCommand.Execute(null);
        }
    }

    private void OnExplainAnalyzeClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.ActiveTab is { } query && query.ExplainAnalyzeCommand.CanExecute(null))
        {
            query.ExplainAnalyzeCommand.Execute(null);
        }
    }

    // Export / Import live on the command bar (not the results card), so their
    // buttons stay here and delegate to the results panel that owns the grid and
    // the file I/O — mirroring how the editor's Find hands off to QueryEditorPanel.
    private void OnExportCsvClick(object? sender, RoutedEventArgs e) => ResultsPanel.ExportCsv();

    private void OnExportJsonClick(object? sender, RoutedEventArgs e) => ResultsPanel.ExportJson();

    private void OnImportClick(object? sender, RoutedEventArgs e) => ResultsPanel.Import();

    // "Review & commit…" on the staged-changes status segment: show the
    // generated SQL, then commit it all as one transaction or discard it all.
    private async void OnReviewPendingClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.ActiveTab is not { } query || query.PendingChanges is not { IsEmpty: false } pending)
        {
            return;
        }

        // Long-form summary here; the status bar's text is deliberately terse.
        var summary = Strings.Shell_StagedSummary(pending.Count, $"{pending.Schema}.{pending.Table}");
        var dialog = new PendingChangesDialog(summary, pending.BuildScript(), pending.Count, pending.UncheckedColumns);
        var result = await dialog.ShowDialog<PendingChangesDialog.Result>(this);
        switch (result)
        {
            case PendingChangesDialog.Result.Commit:
                await query.CommitPendingCommand.ExecuteAsync(null);
                if (query.LastCommitConflict is { } conflict)
                {
                    await ShowStagedConflictAsync(query, conflict);
                }

                break;
            case PendingChangesDialog.Result.Discard:
                await query.DiscardPendingCommand.ExecuteAsync(null);
                break;
        }
    }

    // A commit rolled back by the concurrency check: show before / current /
    // proposed, then act on the user's way out. Closing leaves the set staged
    // exactly as it was, with the explanation still in the status bar.
    private async Task ShowStagedConflictAsync(QueryViewModel query, StagedChangesConflictException conflict)
    {
        var choice = await new StagedConflictDialog(conflict).ShowDialog<StagedConflictDialog.Result>(this);
        switch (choice)
        {
            case StagedConflictDialog.Result.Restage:
                await query.RestageAfterConflictAsync();
                break;
            case StagedConflictDialog.Result.UnstageConflicts:
                await query.UnstageConflictsAsync();
                break;
        }
    }

    // Status-bar "Discard": one confirm (it drops real staged work), then
    // clears the set and reloads server values.
    private async void OnDiscardPendingClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.ActiveTab is not { HasPendingChanges: true } query)
        {
            return;
        }

        var count = query.PendingChanges!.Count;
        var confirm = new ConfirmDialog(Strings.Shell_DiscardStagedConfirm(count), Strings.Common_Discard);
        if (await confirm.ShowDialog<bool>(this))
        {
            await query.DiscardPendingCommand.ExecuteAsync(null);
        }
    }

    // --- Open/save .sql files -----------------------------------------------
    // MainViewModel raises the three events below (palette + Ctrl+O/S/Shift+S
    // key bindings all resolve to its commands); this window owns the
    // StorageProvider dialogs and the actual file I/O, same split as
    // Import/Export above.

    /// <summary>
    /// Rebuilds the ☰ menu's "Open recent" submenu from the live recent-files
    /// list — called every time the menu opens. Headers are TextBlocks, not
    /// strings, so an underscore in a file name isn't eaten as an access key;
    /// the full path rides in the tooltip since the item shows only the name.
    /// </summary>
    private void RebuildRecentFilesMenu()
    {
        MenuOpenRecent.Items.Clear();
        if (_viewModel is not { RecentSqlFiles.Count: > 0 } viewModel)
        {
            MenuOpenRecent.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = Strings.Shell_MenuNoRecentFiles },
                IsEnabled = false,
            });
            return;
        }

        foreach (var path in viewModel.RecentSqlFiles)
        {
            var item = new MenuItem { Header = new TextBlock { Text = Path.GetFileName(path) } };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) => _ = OpenRecentFileAsync(path);
            MenuOpenRecent.Items.Add(item);
        }
    }

    private void OnOpenFileRequested() => _ = OpenSqlFileAsync();

    private void OnSaveFileRequested(bool saveAs) => _ = SaveSqlFileAsync(saveAs);

    private void OnSaveQueryRequested(bool saveAsNew) => _ = SaveQueryAsync(saveAsNew);

    private void OnOpenRecentFileRequested(string path) => _ = OpenRecentFileAsync(path);

    /// <summary>Ctrl+O / palette "Open file…": pick a file and load it into a new tab (or focus it if already open).</summary>
    private async Task OpenSqlFileAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Shell_OpenSqlFileTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQL") { Patterns = ["*.sql"] },
                new FilePickerFileType(Strings.Shell_AllFiles) { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (path is null)
        {
            _viewModel.ActiveTab.Status = Strings.Shell_CantOpenNonLocal;
            _viewModel.ActiveTab.HasError = true;
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path);
            _viewModel.OpenFileTab(path, text);
        }
        catch (Exception ex)
        {
            _viewModel.ActiveTab.Status = Strings.Shell_OpenFailed(ex.Message);
            _viewModel.ActiveTab.HasError = true;
        }
    }

    /// <summary>Palette "Recent file" entry: reload a previously opened path, leaving it in the recent list even if it's now missing.</summary>
    private async Task OpenRecentFileAsync(string path)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            _viewModel.ActiveTab.Status = Strings.Shell_FileNotFound(path);
            _viewModel.ActiveTab.HasError = true;
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path);
            _viewModel.OpenFileTab(path, text);
        }
        catch (Exception ex)
        {
            _viewModel.ActiveTab.Status = Strings.Shell_OpenFailed(ex.Message);
            _viewModel.ActiveTab.HasError = true;
        }
    }

    /// <summary>
    /// Ctrl+S / Ctrl+Shift+S / palette "Save tab to file" / "Save tab as…":
    /// writes the active tab's SQL to its associated file, prompting for a
    /// location when it has none yet or <paramref name="saveAs"/> is true.
    /// </summary>
    private async Task SaveSqlFileAsync(bool saveAs)
    {
        if (_viewModel is null)
        {
            return;
        }

        var tab = _viewModel.ActiveTab;
        var path = tab.FilePath;

        if (path is null || saveAs)
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null)
            {
                return;
            }

            var suggestedName = SanitizeFileName(tab.TabTitle) + ".sql";
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Shell_SaveSqlFileTitle,
                SuggestedFileName = suggestedName,
                DefaultExtension = "sql",
                FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }],
            });

            if (file is null)
            {
                return;
            }

            path = file.TryGetLocalPath();
            if (path is null)
            {
                tab.Status = Strings.Shell_CantSaveNonLocal;
                tab.HasError = true;
                return;
            }
        }

        try
        {
            await File.WriteAllTextAsync(path, tab.Sql);
            tab.MarkSaved(path);
            _viewModel.RecordRecentFile(path);
            tab.Status = Strings.Shell_SavedFile(Path.GetFileName(path));
            tab.HasError = false;
        }
        catch (Exception ex)
        {
            tab.Status = Strings.Shell_SaveFailed(ex.Message);
            tab.HasError = true;
        }
    }

    /// <summary>
    /// Ctrl+S on a scratch tab, the tab menu's "Save query…", and the palette's
    /// explicit entry all land here: name the query and put it in the Saved
    /// Queries list. Re-saving a tab that already has an entry skips the dialog
    /// entirely and just writes through — that silent path is the point of
    /// keeping <see cref="QueryViewModel.SavedQueryId"/> around, and it is what
    /// makes Ctrl+S feel like Ctrl+S rather than like a prompt every time.
    /// </summary>
    private async Task SaveQueryAsync(bool saveAsNew)
    {
        if (_viewModel is null)
        {
            return;
        }

        var tab = _viewModel.ActiveTab;
        var saved = _viewModel.SavedQueries;

        if (string.IsNullOrWhiteSpace(tab.Sql))
        {
            tab.Status = Strings.Shell_NothingToSave;
            tab.HasError = true;
            return;
        }

        // The tab's own entry, if it still exists — a user can delete it from
        // the sidebar while the tab stays open, and a stale id must then behave
        // as "never saved" rather than resurrect a deleted row.
        var existing = !saveAsNew && tab.SavedQueryId is { } id ? saved.FindById(id) : null;

        if (existing is not null)
        {
            var updated = saved.SaveQuery(existing.Name, tab.Sql, existing.Id);
            tab.MarkSavedAsQuery(updated.Id, updated.Name);
            tab.Status = Strings.Shell_SavedQuery(updated.Name);
            tab.HasError = false;
            return;
        }

        var dialog = new SaveQueryDialog(
            saveAsNew ? Strings.Shell_SaveAsNewQueryHeading : Strings.Shell_SaveQueryHeading,
            SaveQueryDialog.SuggestName(tab.TabTitle, saved.FindByName),
            currentId: null,
            saved.FindByName);

        if (await dialog.ShowDialog<SaveQueryResult?>(this) is not { } result)
        {
            return;
        }

        var entry = saved.SaveQuery(result.Name, tab.Sql, result.OverwriteId);
        tab.MarkSavedAsQuery(entry.Id, entry.Name);
        tab.Status = Strings.Shell_SavedQuery(entry.Name);
        tab.HasError = false;
    }

    /// <summary>A usable file-name stem from a tab title: strips characters the filesystem would reject.</summary>
    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. title.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "query" : cleaned;
    }

    // Opens the command palette and moves keyboard focus straight to its search
    // box, so typing filters immediately without a mouse.
    private void OpenCommandPalette()
    {
        if (_viewModel is null)
        {
            return;
        }

        _ = _viewModel.OpenCommandPaletteAsync();
        // Focus after the overlay has been laid out (IsVisible flips this frame).
        Dispatcher.UIThread.Post(() =>
        {
            PaletteSearchBox.Focus();
            PaletteSearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    // Arrow keys move the highlight, Enter runs it, Esc dismisses - all while the
    // caret stays in the search box, so the palette is fully keyboard-driven.
    private void OnPaletteSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var palette = _viewModel.CommandPalette;
        switch (e.Key)
        {
            case Key.Down:
                palette.MoveSelection(+1);
                ScrollPaletteSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                palette.MoveSelection(-1);
                ScrollPaletteSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Enter:
                _ = palette.AcceptAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                palette.CloseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void ScrollPaletteSelectionIntoView()
    {
        if (_viewModel?.CommandPalette.SelectedItem is { } item)
        {
            PaletteList.ScrollIntoView(item);
        }
    }

    // A click on a result row accepts it immediately (the binding has already
    // updated SelectedItem by the time this fires).
    private void OnPaletteListTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is not null && _viewModel.CommandPalette.SelectedItem is not null)
        {
            _ = _viewModel.CommandPalette.AcceptAsync();
        }
    }

    // A press on the scrim (but not the card) dismisses the palette.
    /// <summary>The command bar's centered search pill — same target as Ctrl+K / Ctrl+P.</summary>
    private void OnPaletteSearchButtonClick(object? sender, RoutedEventArgs e) => OpenCommandPalette();

    private void OnPaletteScrimPressed(object? sender, PointerPressedEventArgs e) =>
        _viewModel?.CommandPalette.CloseCommand.Execute(null);

    // Swallow presses on the card so they don't bubble to the scrim and close it.
    private void OnPaletteCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

}
