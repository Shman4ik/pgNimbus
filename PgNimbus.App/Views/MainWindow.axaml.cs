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
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private ShortcutsWindow? _shortcutsWindow;

    // Sidebar collapse (Ctrl+B): the width to restore to, and whether it's hidden.
    private const double SidebarMinWidth = 200;
    private GridLength _savedSidebarWidth = new(300);
    private bool _sidebarCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
        SetUpMacTitleBar();

        // Gestures use the platform/preference command modifier (Ctrl or Cmd),
        // so they're built in code, and rebuilt live when the scheme changes.
        BuildKeyBindings();
        Hotkeys.Changed += BuildKeyBindings;
        Closed += (_, _) => Hotkeys.Changed -= BuildKeyBindings;

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

        // Drag a tab along the strip to reorder it. Pressed is tunneled so the
        // drag candidate is noted before the ListBoxItem handles selection;
        // moves/releases bubble up from the item that holds the pointer capture.
        TabsList.AddHandler(PointerPressedEvent, OnTabStripPointerPressed, RoutingStrategies.Tunnel);
        TabsList.PointerMoved += OnTabStripPointerMoved;
        TabsList.PointerReleased += (_, _) => EndTabDrag();
        TabsList.PointerCaptureLost += (_, _) => EndTabDrag();

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
    /// macOS-only: merges the command bar with the title bar, TablePlus-style.
    /// The system title bar collapses to just the traffic lights (drawn over
    /// our 40px bar, which matches the standard macOS title bar height), the
    /// bar's left padding keeps the sidebar toggle clear of them, and pressing
    /// the bar's empty space drags the window - buttons mark their presses
    /// handled, so this never swallows a click. No-op everywhere else; on
    /// Windows the title bar is themed by <see cref="ThemedWindowChrome"/>.
    /// </summary>
    private void SetUpMacTitleBar()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // Avalonia 12 keeps the native traffic lights when extending on macOS
        // (the old ExtendClientAreaChromeHints enum is gone).
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 40;
        // 16px original left padding + room for the traffic-light cluster.
        CommandBar.Padding = new Thickness(84, 0, 16, 0);

        // The native menu bar (BuildMacNativeMenu) is the file-command home on
        // macOS, so the in-window ☰ menu would be a second copy of the same
        // commands; the wordmark goes too — the menu bar already says the
        // app's name, and Mac toolbars (TablePlus, Finder) don't repeat it.
        AppMenuButton.IsVisible = false;
        AppTitleText.IsVisible = false;
        ConnectionHostText.Margin = new Thickness(0);
        CommandBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(CommandBar).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // Match the native macOS title bar: single press drags, double
            // click zooms. BeginMoveDrag swallows the second press, so the
            // zoom has to be handled here rather than via DoubleTapped.
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        };
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
    /// macOS-only: the real menu bar — File / Query / View / Help — wired to
    /// the same commands as the (hidden there) in-window ☰ menu, palette, and
    /// key bindings. Rebuilt from BuildKeyBindings so the displayed gestures
    /// track the live Ctrl/Cmd scheme; a fresh NativeMenu each time, so no
    /// handler ever double-subscribes. The app-level menu (About / Settings…)
    /// lives in App.axaml — this covers the window-scoped menus. Commands go
    /// through DelegatedCommand: macOS re-validates items each time a menu
    /// opens, which is when CanExecute is read.
    /// </summary>
    private void BuildMacNativeMenu()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var cmd = Hotkeys.Command;

        var recentMenu = new NativeMenu();
        var fileMenu = new NativeMenu
        {
            Items =
            {
                CommandItem("New Query Tab", () => _viewModel?.AddTabCommand, new KeyGesture(Key.T, cmd)),
                CommandItem("Open .sql File…", () => _viewModel?.OpenFileCommand, new KeyGesture(Key.O, cmd)),
                new NativeMenuItem("Open Recent") { Menu = recentMenu },
                new NativeMenuItemSeparator(),
                CommandItem("Save", () => _viewModel?.SaveFileCommand, new KeyGesture(Key.S, cmd)),
                CommandItem("Save As…", () => _viewModel?.SaveFileAsCommand, new KeyGesture(Key.S, cmd | KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                CommandItem("Close Tab", () => _viewModel?.CloseTabCommand, new KeyGesture(Key.W, cmd)),
                new NativeMenuItemSeparator(),
                CommandItem("Switch Connection…", () => _viewModel?.SwitchConnectionCommand),
                CommandItem("New Connection Window…", () => _viewModel?.OpenNewWindowCommand),
            },
        };
        // Like the ☰ menu's submenu: reflects the list as of menu open.
        fileMenu.NeedsUpdate += (_, _) => RebuildMacRecentMenu(recentMenu);

        var queryMenu = new NativeMenu
        {
            Items =
            {
                CommandItem("Run", () => _viewModel?.ActiveTab?.RunCommand, new KeyGesture(Key.Enter, cmd)),
                CommandItem("Cancel", () => _viewModel?.ActiveTab?.CancelCommand),
                new NativeMenuItemSeparator(),
                CommandItem("Format SQL", () => _viewModel?.FormatSqlCommand),
                new NativeMenuItemSeparator(),
                CommandItem("Refresh Schema", () => _viewModel?.RefreshSchemaCommand, new KeyGesture(Key.R, cmd | KeyModifiers.Shift)),
                CommandItem("Server Activity…", () => _viewModel?.ShowActivityCommand),
                CommandItem("Database Overview…", () => _viewModel?.ShowDatabaseOverviewCommand),
            },
        };

        // Finder-style Show/Hide phrasing, re-resolved every time the menu
        // opens. AppKit appends its own "Enter Full Screen" item to the menu
        // titled "View", so full screen isn't added here.
        var sidebarItem = ActionItem("Hide Sidebar", ToggleSidebar, new KeyGesture(Key.B, cmd));
        var viewMenu = new NativeMenu
        {
            Items =
            {
                ActionItem("Command Palette…", OpenCommandPalette, new KeyGesture(Key.K, cmd)),
                new NativeMenuItemSeparator(),
                sidebarItem,
                ActionItem("Toggle Light/Dark Theme", ToggleTheme),
                new NativeMenuItemSeparator(),
                ActionItem("Keyboard Shortcuts", ShowShortcutsWindow, new KeyGesture(Key.F1)),
            },
        };
        viewMenu.NeedsUpdate += (_, _) => sidebarItem.Header = _sidebarCollapsed ? "Show Sidebar" : "Hide Sidebar";

        var windowMenu = new NativeMenu
        {
            Items =
            {
                ActionItem("Minimize", () => WindowState = WindowState.Minimized, new KeyGesture(Key.M, cmd)),
                ActionItem("Zoom", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
            },
        };

        // No Help menu on purpose: AppKit force-inserts a search field into
        // any menu named "Help" (it searches a help book this app doesn't
        // have), so its would-be items live in View (shortcuts) and the app
        // menu (GitHub link) instead.
        NativeMenu.SetMenu(this, new NativeMenu
        {
            Items =
            {
                new NativeMenuItem("File") { Menu = fileMenu },
                new NativeMenuItem("Query") { Menu = queryMenu },
                new NativeMenuItem("View") { Menu = viewMenu },
                new NativeMenuItem("Window") { Menu = windowMenu },
            },
        });
    }

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

    private static NativeMenuItem ActionItem(string header, Action action, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>File → Open Recent, same contract as <see cref="RebuildRecentFilesMenu"/>.</summary>
    private void RebuildMacRecentMenu(NativeMenu menu)
    {
        menu.Items.Clear();
        if (_viewModel is not { RecentSqlFiles.Count: > 0 } viewModel)
        {
            menu.Items.Add(new NativeMenuItem("No Recent Files") { IsEnabled = false });
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
        Add(new KeyGesture(Key.Enter, Hotkeys.Command), () => _viewModel?.ActiveTab?.RunCommand);
        Add(new KeyGesture(Key.F5), () => _viewModel?.ActiveTab?.RunCommand);
        Add(new KeyGesture(Key.Escape), () => _viewModel?.ActiveTab?.CancelCommand);
        Add(new KeyGesture(Key.T, Hotkeys.Command), () => _viewModel?.AddTabCommand);
        // No parameter: CloseTab falls back to the active tab.
        Add(new KeyGesture(Key.W, Hotkeys.Command), () => _viewModel?.CloseTabCommand);
        Add(new KeyGesture(Key.PageDown, Hotkeys.Command), () => _viewModel?.NextTabCommand);
        Add(new KeyGesture(Key.PageUp, Hotkeys.Command), () => _viewModel?.PreviousTabCommand);
        Add(new KeyGesture(Key.R, Hotkeys.Command | KeyModifiers.Shift), () => _viewModel?.RefreshSchemaCommand);
        // Ctrl/Cmd+, — the near-universal preferences shortcut.
        Add(new KeyGesture(Key.OemComma, Hotkeys.Command), () => _viewModel?.ShowPreferencesCommand);
        Add(new KeyGesture(Key.A, Hotkeys.Command | KeyModifiers.Shift), () => _viewModel?.ToggleAutoAliasCommand);
        Add(new KeyGesture(Key.O, Hotkeys.Command), () => _viewModel?.OpenFileCommand);
        Add(new KeyGesture(Key.S, Hotkeys.Command), () => _viewModel?.SaveFileCommand);
        Add(new KeyGesture(Key.S, Hotkeys.Command | KeyModifiers.Shift), () => _viewModel?.SaveFileAsCommand);

        // The gear button's tooltip carries the shortcut, so it's set here
        // (not in XAML) to track the live Ctrl/Cmd scheme.
        ToolTip.SetTip(PreferencesButton, $"Preferences ({Hotkeys.Label(",")})");

        // Same for the ☰ menu's shortcut captions: display-only gestures whose
        // Ctrl/Cmd side must match the bindings built just above.
        MenuNewTab.InputGesture = new KeyGesture(Key.T, Hotkeys.Command);
        MenuOpenFile.InputGesture = new KeyGesture(Key.O, Hotkeys.Command);
        MenuSaveFile.InputGesture = new KeyGesture(Key.S, Hotkeys.Command);
        MenuSaveFileAs.InputGesture = new KeyGesture(Key.S, Hotkeys.Command | KeyModifiers.Shift);
        MenuCloseTab.InputGesture = new KeyGesture(Key.W, Hotkeys.Command);
        MenuPreferences.InputGesture = new KeyGesture(Key.OemComma, Hotkeys.Command);

        // And the search pill's caption (the palette itself opens from
        // OnKeyDown, which reads Hotkeys.Command live).
        PaletteSearchShortcut.Text = Hotkeys.Label("K");

        void Add(KeyGesture gesture, Func<System.Windows.Input.ICommand?> resolve) =>
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new DelegatedCommand(resolve) });
    }

    // F6 hops focus between the SQL editor and the results grid (the two
    // keyboard workspaces). Done in code because the target depends on where
    // focus currently is - a KeyBinding can't express a toggle.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel?.CellInspector.IsOpen == true)
        {
            _viewModel.CellInspector.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.K || e.Key == Key.P) && e.KeyModifiers.HasFlag(Hotkeys.Command))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None)
        {
            ShowShortcutsWindow();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.B && e.KeyModifiers == Hotkeys.Command)
        {
            ToggleSidebar();
            e.Handled = true;
            return;
        }

        // Find / find & replace in the SQL editor. Handled here rather than a
        // KeyBinding so the Cmd scheme works even though SearchPanel.Install
        // also binds the physical Ctrl+F internally.
        if (e.Key == Key.F && e.KeyModifiers == Hotkeys.Command)
        {
            QueryEditor.OpenSearch(replaceMode: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.H && e.KeyModifiers == Hotkeys.Command)
        {
            QueryEditor.OpenSearch(replaceMode: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6 && e.KeyModifiers == KeyModifiers.None)
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

    private void Attach(MainViewModel vm)
    {
        if (_viewModel is not null)
        {
            _viewModel.ThemeToggleRequested -= ToggleTheme;
            _viewModel.ShortcutsRequested -= ShowShortcutsWindow;
            _viewModel.SwitchConnectionRequested -= SwitchConnection;
            _viewModel.NewWindowRequested -= OpenNewWindow;
            _viewModel.ImportPlanRequested -= ShowImportPlanDialog;
        _viewModel.ActivityRequested -= ShowActivityWindow;
            _viewModel.DatabaseOverviewRequested -= ShowDatabaseOverviewWindow;
            _viewModel.SidebarToggleRequested -= ToggleSidebar;
            _viewModel.PreferencesRequested -= ShowPreferencesWindow;
            _viewModel.OpenFileRequested -= OnOpenFileRequested;
            _viewModel.SaveFileRequested -= OnSaveFileRequested;
            _viewModel.OpenRecentFileRequested -= OnOpenRecentFileRequested;
        }

        _viewModel = vm;
        // Palette actions that touch the window are handled here.
        _viewModel.ThemeToggleRequested += ToggleTheme;
        _viewModel.ShortcutsRequested += ShowShortcutsWindow;
        _viewModel.SwitchConnectionRequested += SwitchConnection;
        _viewModel.NewWindowRequested += OpenNewWindow;
        // FormatSqlRequested / ExpandStarRequested / FindRequested are handled by
        // QueryEditorPanel, which subscribes to them off its own DataContext.
        _viewModel.ImportPlanRequested += ShowImportPlanDialog;
        _viewModel.ActivityRequested += ShowActivityWindow;
        _viewModel.DatabaseOverviewRequested += ShowDatabaseOverviewWindow;
        _viewModel.SidebarToggleRequested += ToggleSidebar;
        _viewModel.PreferencesRequested += ShowPreferencesWindow;
        _viewModel.OpenFileRequested += OnOpenFileRequested;
        _viewModel.SaveFileRequested += OnSaveFileRequested;
        _viewModel.OpenRecentFileRequested += OnOpenRecentFileRequested;

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

        // A press on the tab's ✕ chip is a close click, never a drag.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
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

    private void OnShowShortcutsClick(object? sender, RoutedEventArgs e) => ShowShortcutsWindow();

    private void OnSwitchConnectionClick(object? sender, RoutedEventArgs e) => SwitchConnection();

    // Reopens the connection dialog so a different profile (or an ad-hoc
    // connection) can be chosen without restarting the app. This window stays
    // usable until the new connection succeeds; only then does the dialog's
    // Connected handler close it (tearing down its notify listener/tunnel via
    // the Closed hook in App.BuildMainWindow). Cancelling the dialog leaves
    // everything as it was.
    private void SwitchConnection()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
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

    private void ShowShortcutsWindow()
    {
        // Reuse the open instance instead of stacking copies when F1 is
        // pressed twice (the window nulls the field on close).
        if (_shortcutsWindow is not null)
        {
            _shortcutsWindow.Activate();
            return;
        }

        _shortcutsWindow = new ShortcutsWindow();
        _shortcutsWindow.Closed += (_, _) => _shortcutsWindow = null;
        _shortcutsWindow.Show(this);
    }

    private PreferencesWindow? _preferencesWindow;

    private void OnShowPreferencesClick(object? sender, RoutedEventArgs e) => ShowPreferencesWindow();

    // One live instance, same pattern as the shortcuts window.
    private void ShowPreferencesWindow()
    {
        if (_preferencesWindow is not null)
        {
            _preferencesWindow.Activate();
            return;
        }

        if (_viewModel is null)
        {
            return;
        }

        _preferencesWindow = new PreferencesWindow { DataContext = new PreferencesViewModel(_viewModel) };
        _preferencesWindow.Closed += (_, _) => _preferencesWindow = null;
        _preferencesWindow.Show(this);
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
        var summary = $"{pending.Count} staged change{(pending.Count == 1 ? "" : "s")} · {pending.Schema}.{pending.Table}";
        var dialog = new PendingChangesDialog(summary, pending.BuildScript(), pending.Count);
        var result = await dialog.ShowDialog<PendingChangesDialog.Result>(this);
        switch (result)
        {
            case PendingChangesDialog.Result.Commit:
                await query.CommitPendingCommand.ExecuteAsync(null);
                break;
            case PendingChangesDialog.Result.Discard:
                await query.DiscardPendingCommand.ExecuteAsync(null);
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
        var noun = count == 1 ? "1 staged change" : $"{count} staged changes";
        var confirm = new ConfirmDialog($"Discard {noun}? The database hasn't been touched.", "Discard");
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
                Header = new TextBlock { Text = "No recent files" },
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

    private void OnOpenRecentFileRequested(string path) => _ = OpenRecentFileAsync(path);

    /// <summary>Ctrl+O / palette "Open .sql file…": pick a file and load it into a new tab (or focus it if already open).</summary>
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
            Title = "Open SQL file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQL") { Patterns = ["*.sql"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (path is null)
        {
            _viewModel.ActiveTab.Status = "Can't open a non-local file";
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
            _viewModel.ActiveTab.Status = $"Open failed: {ex.Message}";
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
            _viewModel.ActiveTab.Status = $"File not found: {path}";
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
            _viewModel.ActiveTab.Status = $"Open failed: {ex.Message}";
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
                Title = "Save SQL file",
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
                tab.Status = "Can't save to a non-local file";
                tab.HasError = true;
                return;
            }
        }

        try
        {
            await File.WriteAllTextAsync(path, tab.Sql);
            tab.MarkSaved(path);
            _viewModel.RecordRecentFile(path);
            tab.Status = $"Saved {Path.GetFileName(path)}";
            tab.HasError = false;
        }
        catch (Exception ex)
        {
            tab.Status = $"Save failed: {ex.Message}";
            tab.HasError = true;
        }
    }

    /// <summary>A usable file-name stem from a tab title: strips characters the filesystem would reject.</summary>
    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
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
