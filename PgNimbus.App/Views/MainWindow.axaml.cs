using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Import;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private QueryViewModel? _queryViewModel;
    // Re-entrancy guard for the cell inspector's JSON editor: the
    // ViewModel↔AvaloniaEdit two-way sync is manual (AvaloniaEdit's Text isn't a
    // bindable AvaloniaProperty). The SQL editor's own sync now lives in
    // QueryEditorPanel.
    private bool _suppressInspectorSync;
    private object?[]? _pendingEditRow;
    private int _pendingEditColumnIndex;
    private string? _pendingEditText;
    // The editor's text at the moment editing began, so a commit that ends up
    // equal to it is recognized as "no real change" and skipped (see
    // OnCellEditEnded). Null means the baseline is unknown (skip the guard).
    private string? _pendingEditBaselineText;
    private ShortcutsWindow? _shortcutsWindow;
    // JSON highlighting for the cell inspector's edit mode (theme-neutral palette).
    private IHighlightingDefinition? _jsonHighlighting;
    // The row/column the pointer last pressed in the results grid, so "Inspect
    // cell…" on the context menu (which carries no cell of its own) knows what
    // to open - kept in sync by every press, not just the one that opens it.
    private object?[]? _lastPressedRow;
    private int _lastPressedColumnIndex;
    // True while a cell editor is open in the results grid, so the Space
    // quick-peek (OnResultsGridKeyDown) never fires while the editor owns the
    // space bar - a TextBox doesn't mark Space's KeyDown handled, it inserts
    // the space on TextInput, so the key would otherwise bubble up here.
    private bool _isCellEditing;
    // One-shot: set when a double-click lands on an editable json/jsonb cell,
    // consumed by OnResultsGridBeginningEdit to cancel the grid's inline edit so
    // the click opens the cell inspector's JSON editor instead (see
    // OnResultsGridCellPointerPressed).
    private bool _suppressJsonInlineEdit;

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
            ApplyJsonHighlightingTheme();
            UpdateThemeIcon();
            // ShellBackdropBrush is theme-split, so re-resolve on a theme flip.
            ApplyBackdrop();
        };
        // The backdrop material only renders while the window is active - swap
        // the shell base between translucent and opaque on focus changes.
        Activated += (_, _) => ApplyBackdrop();
        Deactivated += (_, _) => ApplyBackdrop();

        // Cell-inspector JSON editor: same manual two-way sync as the SQL editor
        // (which lives in QueryEditorPanel now).
        // Editor → ViewModel here; ViewModel → editor in OnCellInspectorPropertyChanged.
        JsonInspectorEditor.TextChanged += (_, _) =>
        {
            if (_viewModel is null || _suppressInspectorSync)
            {
                return;
            }

            _viewModel.CellInspector.EditText = JsonInspectorEditor.Text;
        };
        LoadJsonHighlighting();

        ResultsGrid.CellEditEnding += OnCellEditEnding;
        ResultsGrid.CellEditEnded += OnCellEditEnded;
        ResultsGrid.BeginningEdit += OnResultsGridBeginningEdit;
        ResultsGrid.PreparingCellForEdit += OnPreparingCellForEdit;
        ResultsGrid.KeyDown += OnResultsGridKeyDown;
        ResultsGrid.Sorting += OnResultsGridSorting;
        // Safe mode's dirty-row wash: rows are tinted as the grid realizes
        // them; already-realized rows are re-tinted whenever the staged set
        // changes (see RefreshPendingRowHighlights).
        ResultsGrid.LoadingRow += (_, e) => ApplyRowStaging(e.Row);

        // The FK-navigation items are composed per-cell just before the grid
        // context menu shows (their targets depend on which cell was pressed).
        if (ResultsGrid.ContextMenu is { } gridMenu)
        {
            gridMenu.Opening += (_, _) => OnResultsGridMenuOpening();
        }

        // Lock the cell inspector's JSON editor selection wash to the fixed
        // brand-blue token (AppTextSelectionBrush in Theme.axaml, shared with
        // every plain TextBox's Style setter), so a selection there reads
        // identically to the SQL editor and every plain TextBox. SelectionBrush
        // lives on TextArea, not TextEditor, so it can't be a XAML attribute.
        // (The SQL editor sets its own the same way, in QueryEditorPanel.)
        if (this.TryFindResource("AppTextSelectionBrush", out var selectionBrush)
            && selectionBrush is IBrush brush)
        {
            JsonInspectorEditor.TextArea.SelectionBrush = brush;
        }

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

    // The cell inspector's JSON editor gets its own highlighting, theme-rewritten
    // the same way the SQL editor's is in QueryEditorPanel (the XSHD bakes in the
    // dark palette).
    private void LoadJsonHighlighting()
    {
        using var stream = AssetLoader.Open(new Uri("avares://PgNimbus.App/Assets/Json.xshd"));
        using var reader = XmlReader.Create(stream);
        _jsonHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        ApplyJsonHighlightingTheme();
    }

    private void ApplyJsonHighlightingTheme()
    {
        if (_jsonHighlighting is null)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        SetHighlightColor(_jsonHighlighting, "Property", dark ? "#9CDCFE" : "#0451A5");
        SetHighlightColor(_jsonHighlighting, "String", dark ? "#CE9178" : "#A31515");
        SetHighlightColor(_jsonHighlighting, "Number", dark ? "#B5CEA8" : "#098658");
        SetHighlightColor(_jsonHighlighting, "Keyword", dark ? "#569CD6" : "#0000E0");

        UpdateInspectorHighlighting();
    }

    // JSON highlighting only applies to a json/jsonb cell — the inspector now
    // edits any free-text type (plain text, arrays, xml, …) where colored JSON
    // tokens would be noise, so a non-JSON cell gets the bare editor. Reassigning
    // (null then set) drops the TextView's cached line visuals so the change takes.
    private void UpdateInspectorHighlighting()
    {
        var json = _viewModel?.CellInspector.IsJson == true ? _jsonHighlighting : null;
        JsonInspectorEditor.SyntaxHighlighting = null;
        JsonInspectorEditor.SyntaxHighlighting = json;
    }

    // ViewModel → editor half of the inspector's manual two-way sync: when the
    // ViewModel changes EditText (entering edit mode, Format, Minify), push it
    // into AvaloniaEdit under the re-entrancy guard so the echo back doesn't loop.
    private void OnCellInspectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IsJson flips per opened cell; the editor's highlighting follows it.
        if (e.PropertyName == nameof(CellInspectorViewModel.IsJson))
        {
            UpdateInspectorHighlighting();
            return;
        }

        if (e.PropertyName != nameof(CellInspectorViewModel.EditText) || _viewModel is null)
        {
            return;
        }

        var text = _viewModel.CellInspector.EditText;
        if (JsonInspectorEditor.Text == text)
        {
            return;
        }

        _suppressInspectorSync = true;
        JsonInspectorEditor.Text = text;
        _suppressInspectorSync = false;
    }

    private static void SetHighlightColor(IHighlightingDefinition? highlighting, string name, string hex)
    {
        if (highlighting?.GetNamedColor(name) is { } color)
        {
            color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
        }
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
                ResultsGrid.Focus();
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
            _viewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            _viewModel.CellInspector.PropertyChanged -= OnCellInspectorPropertyChanged;
            _viewModel.ThemeToggleRequested -= ToggleTheme;
            _viewModel.ShortcutsRequested -= ShowShortcutsWindow;
            _viewModel.SwitchConnectionRequested -= SwitchConnection;
            _viewModel.NewWindowRequested -= OpenNewWindow;
            _viewModel.ActivityRequested -= ShowActivityWindow;
            _viewModel.DatabaseOverviewRequested -= ShowDatabaseOverviewWindow;
            _viewModel.SidebarToggleRequested -= ToggleSidebar;
            _viewModel.PreferencesRequested -= ShowPreferencesWindow;
            _viewModel.OpenFileRequested -= OnOpenFileRequested;
            _viewModel.SaveFileRequested -= OnSaveFileRequested;
            _viewModel.OpenRecentFileRequested -= OnOpenRecentFileRequested;
        }

        _viewModel = vm;
        _viewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        _viewModel.CellInspector.PropertyChanged += OnCellInspectorPropertyChanged;
        // Palette actions that touch the window are handled here.
        _viewModel.ThemeToggleRequested += ToggleTheme;
        _viewModel.ShortcutsRequested += ShowShortcutsWindow;
        _viewModel.SwitchConnectionRequested += SwitchConnection;
        _viewModel.NewWindowRequested += OpenNewWindow;
        // FormatSqlRequested / ExpandStarRequested / FindRequested are handled by
        // QueryEditorPanel, which subscribes to them off its own DataContext.
        _viewModel.ActivityRequested += ShowActivityWindow;
        _viewModel.DatabaseOverviewRequested += ShowDatabaseOverviewWindow;
        _viewModel.SidebarToggleRequested += ToggleSidebar;
        _viewModel.PreferencesRequested += ShowPreferencesWindow;
        _viewModel.OpenFileRequested += OnOpenFileRequested;
        _viewModel.SaveFileRequested += OnSaveFileRequested;
        _viewModel.OpenRecentFileRequested += OnOpenRecentFileRequested;

        // Warm the FK cache in the background so the grid's FK-navigation menu
        // items (which can't await) have edges to read by the time it's opened.
        _ = vm.EnsureForeignKeysAsync();

        AttachQuery(vm.ActiveTab);
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ActiveTab is transiently null while the tab ListBox reacts to the
        // removal of its selected item (see MainViewModel.CloseTab).
        if (e.PropertyName == nameof(MainViewModel.ActiveTab) && _viewModel is { ActiveTab: not null })
        {
            AttachQuery(_viewModel.ActiveTab);
        }
    }

    // Switching the active tab swaps which QueryViewModel the shared results grid
    // reflects - each tab keeps its own Rows/Status, but there's only one
    // on-screen grid, so this re-points it at the new tab. (The editor tracks the
    // active tab independently, inside QueryEditorPanel.)
    private void AttachQuery(QueryViewModel query)
    {
        if (_queryViewModel is not null)
        {
            _queryViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _queryViewModel.ColumnNames.CollectionChanged -= OnColumnNamesChanged;
        }

        _queryViewModel = query;
        _queryViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _queryViewModel.ColumnNames.CollectionChanged += OnColumnNamesChanged;

        ResultsGrid.ItemsSource = query.Rows;
        RebuildColumns(query);
        // The new tab's staged set (if any) tints different rows than the old
        // tab's — repaint once its rows have realized.
        Dispatcher.UIThread.Post(RefreshPendingRowHighlights, DispatcherPriority.Background);
    }

    private void OnColumnNamesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns(_queryViewModel!);

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

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        _isCellEditing = true;

        // NULL cells display a "NULL" placeholder through the column's
        // converter - which also pre-fills the cell editor. Clear it so
        // committing an untouched editor can't turn SQL NULL into the
        // literal string "NULL".
        if (e.EditingElement is TextBox textBox
            && e.Row.DataContext is object?[] row
            && e.Column.DisplayIndex < row.Length
            && row[e.Column.DisplayIndex] is null)
        {
            textBox.Text = string.Empty;
        }

        // Remember what the editor showed when editing began (after the NULL
        // clear above, so an untouched NULL cell baselines as empty). A commit
        // that matches this is just a cell that was entered and left without a
        // real change, and OnCellEditEnded skips it.
        _pendingEditBaselineText = ReadEditedValueText(e.EditingElement);
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Column bindings are one-way (see RebuildColumns), so the grid never
        // mutates Row's array elements itself - the edited value has to be
        // read directly off the editing element here, before it's torn down.
        // (DataGridCellEditEndedEventArgs doesn't expose EditingElement.)
        _pendingEditRow = e.Row.DataContext as object?[];
        _pendingEditColumnIndex = e.Column.DisplayIndex;
        _pendingEditText = ReadEditedValueText(e.EditingElement);
    }

    /// <summary>
    /// The committed value of a cell editor as the canonical text the edit
    /// pipeline expects — the typed editors ResultTextColumn generates for
    /// enum/boolean/date/timestamp columns all reduce to text here, so
    /// CommitCellEditAsync stays a single text-in path. Null means "no value
    /// chosen" (an untouched picker/dropdown), which skips the commit.
    /// </summary>
    private static string? ReadEditedValueText(Control? editingElement) => editingElement switch
    {
        TextBox textBox => textBox.Text,
        CheckBox checkBox => checkBox.IsChecked switch { true => "true", false => "false", null => null },
        ComboBox comboBox => comboBox.SelectedItem as string,
        CalendarDatePicker datePicker =>
            datePicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        StackPanel timestampEditor => ReadTimestampEditorText(timestampEditor),
        _ => null,
    };

    // The timestamp editor is a date picker + time TextBox; no date picked
    // means no value, a blank time means midnight.
    private static string? ReadTimestampEditorText(StackPanel editor)
    {
        if (editor.Children.OfType<CalendarDatePicker>().FirstOrDefault()?.SelectedDate is not { } date)
        {
            return null;
        }

        var time = editor.Children.OfType<TextBox>().FirstOrDefault()?.Text?.Trim();
        return $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {(string.IsNullOrEmpty(time) ? "00:00:00" : time)}";
    }

    private async void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        _isCellEditing = false;

        var row = _pendingEditRow;
        var columnIndex = _pendingEditColumnIndex;
        var text = _pendingEditText;
        var baseline = _pendingEditBaselineText;
        _pendingEditRow = null;
        _pendingEditText = null;
        _pendingEditBaselineText = null;

        if (_queryViewModel is null || row is null || text is null || e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        // No real change: the committed text is identical to what the editor
        // started with (a cell entered and left, or tabbed through). Skip it so
        // it never runs a no-op UPDATE or stages a spurious "edit".
        if (string.Equals(text, baseline, StringComparison.Ordinal))
        {
            return;
        }

        await _queryViewModel.CommitCellEditAsync(row, columnIndex, text);
    }

    // --- Safe mode: dirty-row highlighting ---------------------------------

    // Translucent washes so grid lines and the selection state stay readable
    // in both themes: amber = staged edit, red = staged delete.
    private static readonly IBrush StagedEditRowBrush = new SolidColorBrush(Color.Parse("#38D9822B"));
    private static readonly IBrush StagedDeleteRowBrush = new SolidColorBrush(Color.Parse("#38E03131"));

    private void ApplyRowStaging(DataGridRow row)
    {
        var staging = _queryViewModel is { } query && row.DataContext is object?[] values
            ? query.GetRowStaging(values)
            : QueryViewModel.RowStagingState.None;

        // Always assign: rows are recycled, so a formerly staged row must be
        // washed back to the theme's transparent default.
        row.Background = staging switch
        {
            QueryViewModel.RowStagingState.Edited => StagedEditRowBrush,
            QueryViewModel.RowStagingState.Deleted => StagedDeleteRowBrush,
            _ => Brushes.Transparent,
        };
    }

    // Re-tints every realized row; newly realized ones are handled by the
    // grid's LoadingRow hook. Called whenever the staged set changes.
    private void RefreshPendingRowHighlights()
    {
        foreach (var row in ResultsGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            ApplyRowStaging(row);
        }
    }

    // Ctrl+C copies the selection as TSV (spreadsheet-friendly). ClipboardCopyMode
    // is None on the grid because our columns bind through a converter with no
    // path, so the stock copy has no cell text to read - we build it ourselves.
    private void OnResultsGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(Hotkeys.Command))
        {
            _ = CopySelectionAsync(QueryViewModel.CopyFormat.Tsv);
            e.Handled = true;
            return;
        }

        // Space quick-peeks the current cell in the inspector (TablePlus-style
        // Quick Look): the fast no-menu path that also works in editable grids,
        // where double-click means "edit" instead. Guarded off while a cell
        // editor is open - the editor owns the space bar there.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !_isCellEditing)
        {
            if (ResultsGrid.SelectedItem is object?[] currentRow && ResultsGrid.CurrentColumn is { } currentColumn)
            {
                OpenCellInspector(currentRow, currentColumn.DisplayIndex);
                e.Handled = true;
            }

            return;
        }

        // Delete key removes selected rows, but only for an editable result set
        // and never while a cell is being edited (let the editor keep the key).
        if (e.Key == Key.Delete && _queryViewModel is { IsEditable: true } && !ResultsGrid.IsReadOnly)
        {
            if (ResultsGrid.SelectedItems.OfType<object?[]>().Any())
            {
                _ = DeleteSelectedRowsAsync();
                e.Handled = true;
            }
        }
    }

    // Tracks the last-pressed cell for "Inspect cell…" (the context menu click
    // carries no cell of its own). Double-click is the cell's default action,
    // and it depends on the grid's mode: an editable (browse) grid lets the
    // DataGrid's own gesture begin the inline edit - opening the inspector too
    // used to race it and land on either one unpredictably - while a read-only
    // result set (nothing to edit) opens the inspector, the discoverable
    // no-menu path to the full value. Space quick-peeks in both modes (see
    // OnResultsGridKeyDown).
    private void OnResultsGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        // e.Column is null when the press lands off a real cell (the filler
        // column past the last one, or the empty area below the rows) - nothing
        // to inspect there, so bail before dereferencing it.
        if (e.Row.DataContext is not object?[] row || e.Column is null)
        {
            return;
        }

        _lastPressedRow = row;
        _lastPressedColumnIndex = e.Column.DisplayIndex;

        if (e.PointerPressedEventArgs.ClickCount == 2)
        {
            if (ResultsGrid.IsReadOnly)
            {
                OpenCellInspector(row, e.Column.DisplayIndex);
            }
            else if (IsJsonColumn(e.Column.DisplayIndex))
            {
                // json/jsonb is unusable in a one-line inline editor, so a
                // double-click opens the cell inspector's JSON editor instead.
                // The DataGrid begins its own inline edit from this same
                // gesture (marking the pointer event handled doesn't stop it),
                // so flag the imminent BeginningEdit to cancel it.
                _suppressJsonInlineEdit = true;
                OpenCellInspector(row, e.Column.DisplayIndex, startEditing: true);
            }
        }
    }

    // json/jsonb cells route double-click to the inspector rather than inline
    // editing - the editor metadata is the same Json classification the
    // inspector's own edit path keys off (see OpenCellInspector).
    private bool IsJsonColumn(int columnIndex)
    {
        if (_queryViewModel is not { } query || columnIndex >= query.ColumnNames.Count)
        {
            return false;
        }

        var name = query.ColumnNames[columnIndex];
        return query.EditContext?.Column(name)?.Editor == ColumnValueEditor.Json;
    }

    // Which editor kinds the cell inspector can edit: the free-text ones, all of
    // which reduce to typing a value the commit path converts/casts. The
    // typed-widget kinds (boolean/enum/date/timestamp) are excluded - a plain
    // text box would be a downgrade from their inline checkbox/dropdown/picker.
    private static bool IsFreeTextEditor(ColumnValueEditor editor) => editor is
        ColumnValueEditor.Text or ColumnValueEditor.Array or ColumnValueEditor.Composite
        or ColumnValueEditor.Json or ColumnValueEditor.CastText;

    // Cancels the inline edit the DataGrid tries to start after a double-click
    // on a json/jsonb cell - OnResultsGridCellPointerPressed has already opened
    // the inspector for it.
    private void OnResultsGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_suppressJsonInlineEdit)
        {
            _suppressJsonInlineEdit = false;
            e.Cancel = true;
        }
    }

    private void OnInspectCellClick(object? sender, RoutedEventArgs e)
    {
        if (_lastPressedRow is { } row)
        {
            OpenCellInspector(row, _lastPressedColumnIndex);
        }
    }

    // "Set cell to NULL" acts on the same last-pressed cell the inspector
    // uses - inline editing can't express NULL (empty editor text is an
    // empty string), so this is the explicit gesture.
    private void OnSetCellNullClick(object? sender, RoutedEventArgs e)
    {
        if (_queryViewModel is { } vm && _lastPressedRow is { } row)
        {
            _ = vm.SetCellNullAsync(row, _lastPressedColumnIndex);
        }
    }

    // --- Follow a foreign key from the grid --------------------------------

    // The forward hop staged by the last menu-opening pass, consumed by
    // OnFollowFkClick. (Reverse hops are captured per sub-item closure.)
    private ForeignKeyHop? _followHop;
    // Resolved lazily from the menu's items: named elements inside a
    // ContextMenu aren't reliably reachable through the window's name scope.
    private MenuItem? _followFkItem;
    private MenuItem? _referencingRowsItem;

    // Composes the FK-navigation items for the pressed cell each time the grid
    // menu opens: "Follow <col> → parent" when the cell's column is the child
    // side of an FK, and a "Referencing rows" submenu (one entry per child
    // table) when other tables' FKs point at it. Both hidden otherwise. Reads
    // only the FK cache — a menu can't await a catalog query.
    private void OnResultsGridMenuOpening()
    {
        if (ResultsGrid.ContextMenu is not { } menu)
        {
            return;
        }

        _followFkItem ??= menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "FollowFkMenuItem");
        _referencingRowsItem ??= menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ReferencingRowsMenuItem");
        if (_followFkItem is null || _referencingRowsItem is null)
        {
            return;
        }

        _followHop = null;
        _followFkItem.IsVisible = false;
        _referencingRowsItem.IsVisible = false;
        _referencingRowsItem.ItemsSource = null;

        if (_viewModel is not { } vm || _queryViewModel is not { } query || _lastPressedRow is null)
        {
            return;
        }

        // The table this result set shows: browse mode always knows it; a
        // non-browse result only when it's edit-mapped. Anything else (an
        // arbitrary join, a bare SELECT) has no table identity to hop from.
        var (schema, table) = query.Browse is { } browse
            ? (browse.Schema, browse.Name)
            : query.EditContext is { } ctx ? (ctx.Schema, ctx.Table) : (null!, null!);
        if (schema is null || table is null)
        {
            return;
        }

        if (_lastPressedColumnIndex < 0 || _lastPressedColumnIndex >= query.ColumnNames.Count)
        {
            return;
        }

        var column = query.ColumnNames[_lastPressedColumnIndex];
        var foreignKeys = vm.ForeignKeys;
        if (foreignKeys.Count == 0)
        {
            return;
        }

        if (ForeignKeyNavigator.FindReferencedRow(schema, table, column, foreignKeys) is { } forward)
        {
            _followHop = forward;
            _followFkItem.Header = EscapeMenuHeader($"Follow {column} → {forward.QualifiedTarget}");
            _followFkItem.IsVisible = true;
        }

        var reverse = ForeignKeyNavigator.FindReferencingTables(schema, table, column, foreignKeys);
        if (reverse.Count > 0)
        {
            _referencingRowsItem.ItemsSource = reverse.Select(hop =>
            {
                var item = new MenuItem { Header = EscapeMenuHeader($"{hop.QualifiedTarget} · {string.Join(", ", hop.TargetColumns)}") };
                item.Click += (_, _) => FollowHop(hop);
                return item;
            }).ToList();
            _referencingRowsItem.IsVisible = true;
        }
    }

    // A string MenuItem.Header treats "_" as the access-key marker and eats it
    // ("customer_id" renders as "customerid") — double it so identifiers with
    // underscores display verbatim.
    private static string EscapeMenuHeader(string text) => text.Replace("_", "__");

    private void OnFollowFkClick(object? sender, RoutedEventArgs e)
    {
        if (_followHop is { } hop)
        {
            FollowHop(hop);
        }
    }

    // Jumps to the hop's target table in browse mode, filtered to the rows the
    // pressed row's key values select — a new tab, like every table preview.
    private void FollowHop(ForeignKeyHop hop)
    {
        if (_viewModel is not { } vm || _queryViewModel is not { } query || _lastPressedRow is not { } row)
        {
            return;
        }

        var values = new object?[hop.SourceColumns.Count];
        for (var i = 0; i < hop.SourceColumns.Count; i++)
        {
            var index = query.ColumnNames.IndexOf(hop.SourceColumns[i]);
            if (index < 0 || index >= row.Length)
            {
                query.Status = $"Can't follow: column {hop.SourceColumns[i]} isn't in this result set.";
                return;
            }

            values[i] = row[index];
        }

        if (ForeignKeyNavigator.BuildFilter(hop.TargetColumns, values) is not { } filter)
        {
            query.Status = "Can't follow a NULL key — it references no row.";
            return;
        }

        _ = vm.PreviewTableAsync(hop.TargetSchema, hop.TargetTable, filter);
    }

    private void OpenCellInspector(object?[] row, int columnIndex, bool startEditing = false)
    {
        if (_viewModel is null || _queryViewModel is null || columnIndex >= row.Length
            || columnIndex >= _queryViewModel.ColumnNames.Count)
        {
            return;
        }

        var query = _queryViewModel;
        var name = query.ColumnNames[columnIndex];

        // A cell is editable in the inspector under the same conditions an inline
        // grid edit is: a keyed, editable result set, a non-PK column, and the
        // table's whole primary key present in the result so the UPDATE can
        // target the row. Only free-text editor types get the inspector's roomy
        // multi-line box - the typed-widget types (boolean/enum/date/timestamp)
        // are strictly better edited inline with their checkbox/dropdown/picker,
        // so they stay inline-only here.
        var editorMeta = query.EditContext?.Column(name);
        var canEdit = query.IsEditable
            && query.EditContext is { } ctx
            && editorMeta is { } meta && IsFreeTextEditor(meta.Editor)
            && !ctx.PrimaryKeyColumns.Contains(name)
            && ctx.PrimaryKeyColumns.All(pk => query.ColumnNames.Contains(pk));

        // Commit through the same path an inline edit uses; return null on
        // success or the resulting status text so the inspector can show it.
        Func<int, string, Task<string?>>? commit = canEdit
            ? async (col, text) => await query.CommitCellEditAsync(row, col, text) ? null : query.Status
            : null;

        var validatesAsJson = editorMeta?.Editor == ColumnValueEditor.Json;
        _viewModel.CellInspector.Open(name, row[columnIndex], columnIndex, canEdit, commit, validatesAsJson, startEditing && canEdit);
    }

    private async void OnCellInspectorCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_viewModel.CellInspector.DisplayText);
        }
        catch
        {
            // Clipboard access can throw if another app holds it locked. This is
            // an async void handler, so an unhandled throw would crash the app —
            // a failed copy is not worth that.
        }
    }

    private void OnCellInspectorScrimPressed(object? sender, PointerPressedEventArgs e) =>
        _viewModel?.CellInspector.CloseCommand.Execute(null);

    // Swallow presses on the card so they don't bubble to the scrim and close it.
    private void OnCellInspectorCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // In browse mode a header click sorts server-side (ORDER BY + reload page 1)
    // instead of the client-side comparer sort - cancel the default and re-query.
    private void OnResultsGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_queryViewModel?.Browse is { } browse && e.Column.Header is string columnName)
        {
            e.Handled = true;
            // A header click re-queries page 1; ignore it while a run is already
            // in flight so it can't start a second concurrent execution.
            if (!_queryViewModel.IsRunning)
            {
                _ = browse.SortByAsync(columnName);
            }
        }
    }

    // "Add row…" - opens the insert dialog for the mapped table; on a successful
    // insert the grid refreshes (browse page reload, or a re-run of the query).
    // In safe mode the dialog stages the INSERT into the tab's pending set
    // instead of executing it.
    private async void OnAddRowClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _queryViewModel is not { EditContext: { } context } query)
        {
            return;
        }

        var addRowViewModel = _viewModel.CreateAddRowViewModel(
            context.Schema,
            context.Table,
            query.ShouldStageChanges ? query.TryStageInsert : null);
        addRowViewModel.Inserted += () => _ = query.RefreshCurrentAsync();

        var dialog = new AddRowDialog { DataContext = addRowViewModel };
        await dialog.ShowDialog(this);
    }

    private async void OnDeleteRowsClick(object? sender, RoutedEventArgs e) => await DeleteSelectedRowsAsync();

    // Confirms, then deletes the selected rows via primary-key-keyed DELETEs.
    // In safe mode there's nothing to confirm — the delete is only staged
    // (and Delete on an already-staged row unstages it), reversible until
    // the set is committed.
    private async Task DeleteSelectedRowsAsync()
    {
        if (_queryViewModel is not { IsEditable: true } query)
        {
            return;
        }

        var rows = ResultsGrid.SelectedItems.OfType<object?[]>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        if (query.ShouldStageChanges)
        {
            await query.DeleteRowsAsync(rows);
            return;
        }

        var noun = rows.Count == 1 ? "this row" : $"these {rows.Count} rows";
        var confirm = new ConfirmDialog($"Delete {noun}? This can't be undone.", "Delete");
        if (await confirm.ShowDialog<bool>(this))
        {
            await query.DeleteRowsAsync(rows);
        }
    }

    // "Review & commit…" on the staged-changes status segment: show the
    // generated SQL, then commit it all as one transaction or discard it all.
    private async void OnReviewPendingClick(object? sender, RoutedEventArgs e)
    {
        if (_queryViewModel is not { } query || query.PendingChanges is not { IsEmpty: false } pending)
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
        if (_queryViewModel is not { HasPendingChanges: true } query)
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

    private void OnCopyCells(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Tsv);

    private void OnCopyAsCsv(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Csv);

    private void OnCopyAsJson(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Json);

    private void OnCopyAsMarkdown(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Markdown);

    private void OnCopyAsInsert(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Insert);

    // Copies the selected rows (or the whole result set when nothing is selected)
    // in the chosen shape.
    private async Task CopySelectionAsync(QueryViewModel.CopyFormat format)
    {
        if (_queryViewModel is null)
        {
            return;
        }

        var selected = ResultsGrid.SelectedItems.OfType<object?[]>().ToList();
        var text = _queryViewModel.CopyRows(format, selected);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    // The Explain toolbar button is a single slot with a flyout (minimalist rule);
    // menu items route to the active tab's commands the same way Export's do.
    private void OnExplainClick(object? sender, RoutedEventArgs e)
    {
        if (_queryViewModel is { } query && query.ExplainCommand.CanExecute(null))
        {
            query.ExplainCommand.Execute(null);
        }
    }

    private void OnExplainAnalyzeClick(object? sender, RoutedEventArgs e)
    {
        if (_queryViewModel is { } query && query.ExplainAnalyzeCommand.CanExecute(null))
        {
            query.ExplainAnalyzeCommand.Execute(null);
        }
    }

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        var query = _queryViewModel;
        if (query is not null)
        {
            // Snapshot on the UI thread; ExportAsync runs the returned writer off it.
            await ExportAsync("csv", "CSV", ["*.csv"], query.CreateCsvExport());
        }
    }

    private async void OnExportJsonClick(object? sender, RoutedEventArgs e)
    {
        var query = _queryViewModel;
        if (query is not null)
        {
            await ExportAsync("json", "JSON", ["*.json"], query.CreateJsonExport());
        }
    }

    // "Import" on the command bar: pick a CSV/JSON file, parse it, and hand a
    // prefilled target-table dialog to the user. On success the schema tree
    // refreshes and the active tab SELECTs the fresh table as visible proof.
    private async void OnImportClick(object? sender, RoutedEventArgs e)
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
            Title = "Import CSV or JSON",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV or JSON") { Patterns = ["*.csv", "*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            var data = files[0].Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? TabularFileParser.ParseJson(text)
                : TabularFileParser.ParseCsv(text);

            if (data.Columns.Count == 0)
            {
                _viewModel.ActiveTab.Status = "Nothing to import — the file has no rows.";
                return;
            }

            var schemas = _viewModel.SchemaTree.Schemas.OfType<SchemaNode>().Select(s => s.Name).ToList();
            var importViewModel = new ImportViewModel(_viewModel.Importer, data, SuggestTableName(files[0].Name), schemas);
            importViewModel.Completed += (schema, table, count) => _ = OnImportCompletedAsync(schema, table, count);

            var dialog = new ImportDialog { DataContext = importViewModel };
            await dialog.ShowDialog<bool>(this);
        }
        catch (Exception ex)
        {
            _viewModel.ActiveTab.Status = $"Import failed: {ex.Message}";
            _viewModel.ActiveTab.HasError = true;
        }
    }

    private async Task OnImportCompletedAsync(string schema, string table, long count)
    {
        if (_viewModel is null)
        {
            return;
        }

        await _viewModel.RefreshSchemaCommand.ExecuteAsync(null);

        var tab = _viewModel.ActiveTab;
        tab.Sql = $"SELECT * FROM {SqlIdentifier.QuoteIfNeeded(schema)}.{SqlIdentifier.QuoteIfNeeded(table)} LIMIT 100;";
        await tab.RunCommand.ExecuteAsync(null);
        tab.Status = $"Imported {count:N0} row{(count == 1 ? "" : "s")} into {schema}.{table}";
    }

    /// <summary>A usable default table name from the file name: lower-cased, non-alphanumerics collapsed to '_'.</summary>
    private static string SuggestTableName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var name = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (name.Length == 0)
        {
            name = "imported";
        }

        return char.IsAsciiDigit(name[0]) ? "t_" + name : name;
    }

    private async Task ExportAsync(string extension, string typeName, string[] patterns, Action<Stream>? write)
    {
        if (write is null || _queryViewModel is null || _queryViewModel.Rows.Count == 0)
        {
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"export.{extension}",
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = patterns }],
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        // The writer was snapshotted on the UI thread; do the (potentially large)
        // formatting + file write off it so the interface stays responsive.
        await Task.Run(() => write(stream));
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_queryViewModel is null)
        {
            return;
        }

        // Rows is swapped wholesale instead of mutated in place (bulk
        // collection-change handling in the DataGrid costs ~200 µs/row; a
        // fresh ItemsSource costs a viewport) - re-point the grid each time.
        if (e.PropertyName == nameof(QueryViewModel.Rows))
        {
            ResultsGrid.ItemsSource = _queryViewModel.Rows;
            // Rows realize after this returns; re-tint once they exist so a
            // reloaded page keeps its staged-row washes.
            Dispatcher.UIThread.Post(RefreshPendingRowHighlights, DispatcherPriority.Background);
            return;
        }

        // Every staged-set mutation re-raises this (even when the summary text
        // is unchanged), making it the one repaint cue for row washes.
        if (e.PropertyName == nameof(QueryViewModel.PendingChangesText))
        {
            RefreshPendingRowHighlights();
            return;
        }

        // The edit context lands after the rows do (a browse page sets it once
        // the run completes), and it carries the per-column type metadata the
        // type-aware cell editors need — so the columns must be rebuilt again.
        if (e.PropertyName == nameof(QueryViewModel.EditContext))
        {
            RebuildColumns(_queryViewModel);
        }

        // The Sql → editor sync now lives in QueryEditorPanel, off its own
        // active-tab tracking.
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

    // The column Binding has an empty Path - it passes the row array straight
    // to RowIndexConverter and never resolves a member by name, so the
    // reflection/dynamic code the analyzers warn about is never exercised.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Pathless binding uses a converter only; no reflection member access.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Pathless binding uses a converter only; no dynamic code.")]
    private void RebuildColumns(QueryViewModel query)
    {
        ResultsGrid.Columns.Clear();

        for (var i = 0; i < query.ColumnNames.Count; i++)
        {
            // In browse mode the edit context knows each column's Postgres
            // type — the column uses it to generate a type-aware cell editor
            // (enum dropdown, checkbox, date picker) instead of a TextBox.
            var name = query.ColumnNames[i];
            var editorMeta = query.EditContext?.Column(name);
            // Prefer the browse-mode format_type spelling ("numeric(12,2)") when
            // known; fall back to the wire name for arbitrary queries. Domains
            // resolve to their base type's family (see ClassifierType).
            var declaredType = editorMeta?.DataType ?? query.ColumnTypeName(i);
            // In browse mode the catalog kind is known, so enum/composite columns
            // get their own icon; an arbitrary query only has the wire type name,
            // where an enum is indistinguishable from Other.
            var category = editorMeta is { } meta
                ? PgTypeCategorizer.CategorizeColumn(declaredType, meta.DomainBaseType, meta.Editor)
                : PgTypeCategorizer.Categorize(PgTypeCategorizer.ClassifierType(declaredType, null));

            ResultsGrid.Columns.Add(new ResultTextColumn(i, editorMeta, category)
            {
                Header = CreateColumnHeader(name, declaredType, category, editorMeta),
                // Avalonia 12's DataGrid infers "read-only" from a column's
                // binding path — and a pathless converter binding (the
                // AOT-safe pattern used here) has none, which silently made
                // every column uneditable after the 11→12 upgrade. An explicit
                // false skips that inference; the getter still ORs in the
                // grid-level IsReadOnly, so non-editable result sets stay
                // locked via the grid binding.
                IsReadOnly = false,
                // Empty path + converter instead of "[i]": indexer paths
                // resolve via reflection, which trips NativeAOT/trimming.
                Binding = new Binding
                {
                    Converter = new Converters.RowIndexConverter(i),
                    Mode = BindingMode.OneWay,
                },
                // No binding path also means no stock sort key - header-click
                // sorting needs an explicit comparer, and with no path to
                // infer sortability from, CanUserSort must be set by hand.
                CustomSortComparer = new RowCellComparer(i),
                CanUserSort = true,
                // Auto width sizes to the widest cell, so one long text value
                // used to blow the column past the viewport; cap it and let
                // the cell inspector carry the full value.
                MaxWidth = 560,
            });
        }
    }

    // A results-grid column header: the type-family icon (when the type has one)
    // plus the column name, with a tooltip carrying the full type, its family,
    // and — in browse mode, where the table's real columns are known — its
    // primary-key / not-null flags. The type name comes from the wire protocol so
    // every result set gets the icon, not just editable ones.
    private static Control CreateColumnHeader(string name, string? displayType, PgTypeCategory category, ColumnDetail? meta)
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            // Numeric cells right-align — sit the header over them so name and
            // digits share an edge.
            HorizontalAlignment = category == PgTypeCategory.Numeric
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left,
        };

        if (Converters.PgTypeVisuals.IconFor(category) is { } icon)
        {
            panel.Children.Add(new PathIcon
            {
                Data = icon,
                Width = 11,
                Height = 11,
                Opacity = 0.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = name,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        if (BuildHeaderTooltip(displayType, category, meta) is { } tip)
        {
            ToolTip.SetTip(panel, tip);
            ToolTip.SetShowDelay(panel, 300);
        }

        return panel;
    }

    // displayType is the declared type shown to the user (a domain keeps its own
    // name); the family label comes from the resolved category, so a domain over
    // citext still reads "· Text".
    private static string? BuildHeaderTooltip(string? displayType, PgTypeCategory category, ColumnDetail? meta)
    {
        if (string.IsNullOrEmpty(displayType))
        {
            return null;
        }

        var family = Converters.PgTypeVisuals.LabelFor(category);
        var text = string.IsNullOrEmpty(family) ? displayType : $"{displayType}  ·  {family}";

        if (meta is not null)
        {
            var flags = new System.Collections.Generic.List<string>(2);
            if (meta.IsPrimaryKey)
            {
                flags.Add("primary key");
            }

            if (meta.NotNull)
            {
                flags.Add("not null");
            }

            if (flags.Count > 0)
            {
                text += "\n" + string.Join("  ·  ", flags);
            }
        }

        return text;
    }
}
