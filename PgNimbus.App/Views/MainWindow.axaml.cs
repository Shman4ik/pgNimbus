using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private QueryViewModel? _queryViewModel;
    private bool _suppressEditorSync;
    private object?[]? _pendingEditRow;
    private int _pendingEditColumnIndex;
    private string? _pendingEditText;
    private CompletionWindow? _completionWindow;
    // Set while an accepted filter-completion writes back into the box, so the
    // resulting TextChanged doesn't immediately re-open the popup on the word we
    // just inserted.
    private bool _suppressFilterCompletion;
    private ShortcutsWindow? _shortcutsWindow;
    private IHighlightingDefinition? _sqlHighlighting;
    // The row/column the pointer last pressed in the results grid, so "Inspect
    // cell…" on the context menu (which carries no cell of its own) knows what
    // to open - kept in sync by every press, not just the one that opens it.
    private object?[]? _lastPressedRow;
    private int _lastPressedColumnIndex;
    private readonly BracketHighlightRenderer _bracketRenderer;

    private const double MinEditorFontSize = 8;
    private const double MaxEditorFontSize = 32;
    private const double DefaultEditorFontSize = 14;

    // Sidebar collapse (Ctrl+B): the width to restore to, and whether it's hidden.
    private const double SidebarMinWidth = 200;
    private GridLength _savedSidebarWidth = new(300);
    private bool _sidebarCollapsed;

    public MainWindow()
    {
        InitializeComponent();

        // Must exist before LoadSqlHighlighting - the theme pass that call
        // triggers also resolves this renderer's brush.
        _bracketRenderer = new BracketHighlightRenderer(SqlEditor.TextArea.TextView);

        LoadSqlHighlighting();
        // ActualThemeVariant isn't final at construction time - re-resolve the
        // palette (and the toggle glyph) once the window opens, and again on
        // any live theme switch, however it originates.
        Opened += (_, _) =>
        {
            ApplySqlHighlightingTheme();
            UpdateThemeIcon();
            ApplyBackdrop();
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplySqlHighlightingTheme();
            UpdateThemeIcon();
            // ShellBackdropBrush is theme-split, so re-resolve on a theme flip.
            ApplyBackdrop();
        };

        SqlEditor.TextChanged += (_, _) =>
        {
            if (_queryViewModel is null || _suppressEditorSync)
            {
                return;
            }

            _queryViewModel.Sql = SqlEditor.Text;
        };

        // TreeViewItem's own DoubleTapped handling (toggling expand) marks the
        // event Handled, so it never reaches a plain `+=` subscription on the
        // parent TreeView. Use AddHandler with handledEventsToo to still see it.
        SchemaTreeView.AddHandler(InputElement.DoubleTappedEvent, OnSchemaTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        // Drag a schema/table/column out of the tree and drop it into the SQL
        // editor as a properly quoted identifier. The drag arms on press and
        // only starts after a small movement threshold, so plain clicks,
        // expander toggles, and double-click previews all behave as before.
        SchemaTreeView.AddHandler(InputElement.PointerPressedEvent, OnSchemaTreePointerPressed, RoutingStrategies.Tunnel);
        SchemaTreeView.AddHandler(InputElement.PointerMovedEvent, OnSchemaTreePointerMoved, RoutingStrategies.Tunnel);
        SchemaTreeView.AddHandler(InputElement.PointerReleasedEvent, (_, _) => _treeDragCandidate = null, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(SqlEditor, true);
        SqlEditor.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver);
        SqlEditor.AddHandler(DragDrop.DropEvent, OnEditorDrop);
        ResultsGrid.CellEditEnding += OnCellEditEnding;
        ResultsGrid.CellEditEnded += OnCellEditEnded;
        ResultsGrid.PreparingCellForEdit += OnPreparingCellForEdit;
        ResultsGrid.KeyDown += OnResultsGridKeyDown;
        ResultsGrid.Sorting += OnResultsGridSorting;

        SqlEditor.TextArea.TextEntered += OnSqlTextEntered;
        SqlEditor.KeyDown += OnSqlEditorKeyDown;

        // Editor niceties: current-line wash (brushes are theme-resolved in
        // ApplySqlHighlightingTheme), matching-bracket highlight, and
        // Ctrl+wheel font zoom. The wheel handler tunnels because the
        // TextView claims wheel events for scrolling before they'd bubble.
        SqlEditor.Options.HighlightCurrentLine = true;
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateBracketHighlight();
        SqlEditor.AddHandler(PointerWheelChangedEvent, OnSqlEditorPointerWheel, RoutingStrategies.Tunnel);

        // Tab-strip navigation extras: the ‹/› arrows only appear when the
        // strip overflows, and the ▾ dropdown lists every open tab with
        // type-to-search for when scrolling would take too long.
        TabsList.Loaded += (_, _) => HookTabStripScroll();
        if (TabListButton.Flyout is Flyout tabFlyout)
        {
            tabFlyout.Opened += (_, _) => OpenTabList();
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

        // Column autocomplete inside the browse WHERE box (see the popup in XAML).
        BrowseFilterBox.TextChanged += OnBrowseFilterTextChanged;
        BrowseFilterBox.LostFocus += (_, _) => CloseFilterCompletion();

        // On Windows, popups are native always-on-top windows, and one left
        // open while the user Alt+Tabs (or clicks) into another program keeps
        // floating above that program. Close every popup we own the moment
        // this window stops being the foreground one.
        Deactivated += (_, _) =>
        {
            _completionWindow?.Close();
            CloseFilterCompletion();
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                Attach(vm);
            }
        };
    }

    private void LoadSqlHighlighting()
    {
        using var stream = AssetLoader.Open(new Uri("avares://PgNimbus.App/Assets/PostgreSql.xshd"));
        using var reader = XmlReader.Create(stream);
        _sqlHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        ApplySqlHighlightingTheme();
    }

    // The XSHD bakes in the dark palette; the highlighter has no theme
    // awareness of its own, so the named colors are rewritten whenever the
    // actual theme variant resolves or changes.
    private void ApplySqlHighlightingTheme()
    {
        if (_sqlHighlighting is null)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        SetHighlightColor("Comment", dark ? "#6A9955" : "#008000");
        SetHighlightColor("String", dark ? "#CE9178" : "#A31515");
        SetHighlightColor("Number", dark ? "#B5CEA8" : "#098658");
        SetHighlightColor("Keyword", dark ? "#569CD6" : "#0000E0");
        SetHighlightColor("Type", dark ? "#4EC9B0" : "#267F99");

        // Editor chrome that has to track the theme with the palette: a
        // barely-there wash on the caret's line (border suppressed - the
        // stock one draws a hard outline box) and a stronger accent-tinted
        // wash behind the matched bracket pair.
        var textView = SqlEditor.TextArea.TextView;
        textView.CurrentLineBackground = new SolidColorBrush(Color.Parse(dark ? "#0DFFFFFF" : "#0D000000"));
        textView.CurrentLineBorder = new Pen(Brushes.Transparent);
        _bracketRenderer.Brush = new SolidColorBrush(Color.Parse(dark ? "#40569CD6" : "#332B5FBF"));

        // Reassigning is what makes the TextView drop its cached line
        // visuals and re-run the highlighter with the new brushes.
        SqlEditor.SyntaxHighlighting = null;
        SqlEditor.SyntaxHighlighting = _sqlHighlighting;
    }

    private void SetHighlightColor(string name, string hex)
    {
        if (_sqlHighlighting!.GetNamedColor(name) is { } color)
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

        if ((e.Key == Key.K || e.Key == Key.P) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
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

        if (e.Key == Key.B && e.KeyModifiers == KeyModifiers.Control)
        {
            ToggleSidebar();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6 && e.KeyModifiers == KeyModifiers.None)
        {
            if (SqlEditor.IsKeyboardFocusWithin)
            {
                ResultsGrid.Focus();
            }
            else
            {
                SqlEditor.TextArea.Focus();
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
            _viewModel.ThemeToggleRequested -= ToggleTheme;
            _viewModel.ShortcutsRequested -= ShowShortcutsWindow;
            _viewModel.SwitchConnectionRequested -= SwitchConnection;
            _viewModel.FormatSqlRequested -= FormatCurrentStatement;
        }

        _viewModel = vm;
        _viewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        // Palette actions that touch the window are handled here.
        _viewModel.ThemeToggleRequested += ToggleTheme;
        _viewModel.ShortcutsRequested += ShowShortcutsWindow;
        _viewModel.SwitchConnectionRequested += SwitchConnection;
        _viewModel.FormatSqlRequested += FormatCurrentStatement;

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

    // Switching the active tab swaps which QueryViewModel the shared editor/grid
    // controls reflect - each tab keeps its own Sql/Rows/Status, but there's only
    // one on-screen editor and grid, so this re-points them at the new tab.
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

        _suppressEditorSync = true;
        SqlEditor.Text = query.Sql;
        _suppressEditorSync = false;

        ResultsGrid.ItemsSource = query.Rows;
        RebuildColumns(query);
    }

    private void OnColumnNamesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns(_queryViewModel!);

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryViewModel tab })
        {
            _viewModel?.CloseTabCommand.Execute(tab);
        }
    }

    // --- Schema-tree drag & drop into the editor --------------------------

    // Armed on press over a draggable node; the drag itself starts only after
    // the pointer moves past a threshold with the button still down. The press
    // args are kept because DoDragDropAsync can only start from them.
    private (Point Origin, string Text, PointerPressedEventArgs PressArgs)? _treeDragCandidate;
    private const double DragThreshold = 4;

    /// <summary>The SQL identifier a tree node drops as, quoted only where a bare name wouldn't round-trip.</summary>
    private static string? DragTextFor(object? node) => node switch
    {
        ColumnNode column => SqlIdentifier.QuoteIfNeeded(column.Name),
        TableNode table => $"{SqlIdentifier.QuoteIfNeeded(table.Schema)}.{SqlIdentifier.QuoteIfNeeded(table.Name)}",
        SchemaNode schema => SqlIdentifier.QuoteIfNeeded(schema.Name),
        _ => null,
    };

    private void OnSchemaTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SchemaTreeView).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var node = (e.Source as Visual)?.DataContext;
        _treeDragCandidate = DragTextFor(node) is { } text
            ? (e.GetPosition(SchemaTreeView), text, e)
            : null;
    }

    private async void OnSchemaTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_treeDragCandidate is not { } candidate)
        {
            return;
        }

        if (!e.GetCurrentPoint(SchemaTreeView).Properties.IsLeftButtonPressed)
        {
            _treeDragCandidate = null;
            return;
        }

        var position = e.GetPosition(SchemaTreeView);
        if (Math.Abs(position.X - candidate.Origin.X) < DragThreshold &&
            Math.Abs(position.Y - candidate.Origin.Y) < DragThreshold)
        {
            return;
        }

        _treeDragCandidate = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(candidate.Text));
        await DragDrop.DoDragDropAsync(candidate.PressArgs, data, DragDropEffects.Copy);
    }

    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        // Live caret preview: the caret tracks the pointer so it's obvious
        // where the identifier will land.
        if (SqlEditor.GetPositionFromPoint(e.GetPosition(SqlEditor)) is { } position)
        {
            SqlEditor.TextArea.Caret.Position = position;
        }

        e.Handled = true;
    }

    private void OnEditorDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetText() is not { Length: > 0 } text)
        {
            return;
        }

        var offset = SqlEditor.GetPositionFromPoint(e.GetPosition(SqlEditor)) is { } position
            ? SqlEditor.Document.GetOffset(position.Location)
            : SqlEditor.CaretOffset;
        SqlEditor.Document.Insert(offset, text);
        SqlEditor.CaretOffset = offset + text.Length;
        SqlEditor.TextArea.Focus();
        e.Handled = true;
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
    // base keeps its opaque chrome tone.
    private void ApplyBackdrop()
    {
        var backdropActive = ActualTransparencyLevel == WindowTransparencyLevel.Mica
            || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;

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

    private void OnRemoveChannelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string channel })
        {
            _viewModel?.NotifyMonitor.RemoveChannelCommand.Execute(channel);
        }
    }

    private void OnAlterTableClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TableNode table } || _viewModel is null)
        {
            return;
        }

        var alterTableViewModel = _viewModel.CreateAlterTableViewModel(table);
        // Same TableNode instance the schema tree displays, so reloading its
        // children in place picks up the ALTER TABLE without a full tree refresh.
        alterTableViewModel.SchemaChanged += () => _ = table.RefreshAsync();

        var dialog = new AlterTableDialog { DataContext = alterTableViewModel };
        dialog.ShowDialog(this);
    }

    // "Source (DDL)" - reconstructs the object's CREATE definition and opens it
    // in a new query tab.
    private async void OnShowSourceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: TableNode table } && _viewModel is not null)
        {
            await _viewModel.ShowSourceAsync(table);
        }
    }

    private void OnSchemaTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Read the node off the tapped TreeViewItem's DataContext rather than
        // SchemaTreeView.SelectedItem: on the very first click of a row that
        // wasn't already selected, SelectedItem can still be stale/null at
        // the point this handler runs.
        var container = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (_viewModel is not null && container?.DataContext is TableNode table)
        {
            _ = _viewModel.PreviewTableAsync(table);
        }
    }

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
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
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Column bindings are one-way (see RebuildColumns), so the grid never
        // mutates Row's array elements itself - the edited text has to be
        // read directly off the editing TextBox here, before it's torn down.
        // (DataGridCellEditEndedEventArgs doesn't expose EditingElement.)
        _pendingEditRow = e.Row.DataContext as object?[];
        _pendingEditColumnIndex = e.Column.DisplayIndex;
        _pendingEditText = (e.EditingElement as TextBox)?.Text;
    }

    private async void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        var row = _pendingEditRow;
        var columnIndex = _pendingEditColumnIndex;
        var text = _pendingEditText;
        _pendingEditRow = null;
        _pendingEditText = null;

        if (_queryViewModel is null || row is null || text is null || e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        await _queryViewModel.CommitCellEditAsync(row, columnIndex, text);
    }

    private void OnSqlTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        var c = e.Text[0];

        // A dot starts member access (alias./table./schema.). Re-trigger even
        // when a bare-identifier list is already open, so it switches to the
        // qualifier's columns instead of staying on the catalog-wide list.
        if (c == '.')
        {
            _completionWindow?.Close();
            ShowCompletion(includeTypedChar: false);
            return;
        }

        if (_completionWindow is not null)
        {
            return;
        }

        if (char.IsLetter(c) || c == '_')
        {
            ShowCompletion(includeTypedChar: true);
            return;
        }

        // The space right after FROM/JOIN/INTO/UPDATE opens the table list
        // unprompted — the one spot where what comes next is most predictable.
        if (c == ' ' && WordBeforeCaretStartsTableClause())
        {
            ShowCompletion(includeTypedChar: false);
        }
    }

    // True when the word just left of the caret (which sits right after the
    // freshly typed space) is a keyword that a table name follows.
    private bool WordBeforeCaretStartsTableClause()
    {
        var text = SqlEditor.Text;
        var end = Math.Min(SqlEditor.CaretOffset, text.Length) - 1; // skip the space
        if (end <= 0)
        {
            return false;
        }

        var start = end;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }

        var word = text.AsSpan(start, Math.Max(end - start, 0));
        return word.Equals("from", StringComparison.OrdinalIgnoreCase)
            || word.Equals("join", StringComparison.OrdinalIgnoreCase)
            || word.Equals("into", StringComparison.OrdinalIgnoreCase)
            || word.Equals("update", StringComparison.OrdinalIgnoreCase);
    }

    private void OnSqlEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            ShowCompletion(includeTypedChar: false);
            e.Handled = true;
            return;
        }

        // Smart execution: runs just the statement the caret sits in (between
        // ;s) rather than the whole tab, so trying one statement out of a
        // multi-statement script doesn't require selecting it by hand first.
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Shift)
        {
            if (_queryViewModel is { } query
                && SqlScriptSplitter.StatementAt(SqlEditor.Text, SqlEditor.CaretOffset) is { } statement)
            {
                _ = query.RunStatementAsync(statement);
            }

            e.Handled = true;
            return;
        }

        // Ctrl+Shift+F: pretty-print the statement the caret sits in, in place.
        if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            FormatCurrentStatement();
            e.Handled = true;
            return;
        }

        // Font-size zoom: Ctrl+= / Ctrl+- step, Ctrl+0 resets (numpad
        // variants included). Ctrl+wheel does the same via the tunneled
        // pointer handler. Shift is tolerated because "Ctrl and +" is
        // physically Ctrl+Shift+= on most layouts.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    AdjustEditorFontSize(+1);
                    e.Handled = true;
                    break;
                case Key.OemMinus or Key.Subtract:
                    AdjustEditorFontSize(-1);
                    e.Handled = true;
                    break;
                case Key.D0 or Key.NumPad0:
                    SqlEditor.FontSize = DefaultEditorFontSize;
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnSqlEditorPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        AdjustEditorFontSize(e.Delta.Y >= 0 ? +1 : -1);
        e.Handled = true;
    }

    private void AdjustEditorFontSize(int delta) =>
        SqlEditor.FontSize = Math.Clamp(SqlEditor.FontSize + delta, MinEditorFontSize, MaxEditorFontSize);

    private void UpdateBracketHighlight() =>
        _bracketRenderer.Update(SqlEditor.Text, SqlEditor.CaretOffset);

    private void ShowCompletion(bool includeTypedChar)
    {
        var data = _viewModel?.CompletionProvider.GetCompletionData(SqlEditor.Text, SqlEditor.CaretOffset);
        if (data is not { Count: > 0 })
        {
            return;
        }

        var completionWindow = new CompletionWindow(SqlEditor.TextArea);
        if (includeTypedChar)
        {
            completionWindow.StartOffset -= 1;
        }

        foreach (var item in data)
        {
            completionWindow.CompletionList.CompletionData.Add(item);
        }

        completionWindow.Show();
        completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow = completionWindow;
    }

    // Pretty-prints the statement under the caret and replaces just that span, so
    // formatting one statement in a multi-statement script leaves the others alone.
    // Puts the caret at the end of the reformatted text. A no-op when the caret
    // isn't in a statement or the formatter left the text unchanged.
    private void FormatCurrentStatement()
    {
        var text = SqlEditor.Text;
        if (SqlScriptSplitter.StatementSpanAt(text, SqlEditor.CaretOffset) is not { } span)
        {
            return;
        }

        var (start, end) = span;
        var formatted = SqlFormatter.Format(text[start..end]);
        if (formatted == text[start..end])
        {
            return;
        }

        SqlEditor.Document.Replace(start, end - start, formatted);
        SqlEditor.CaretOffset = start + formatted.Length;
    }

    // Ctrl+C copies the selection as TSV (spreadsheet-friendly). ClipboardCopyMode
    // is None on the grid because our columns bind through a converter with no
    // path, so the stock copy has no cell text to read - we build it ourselves.
    private void OnResultsGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopySelectionAsync(QueryViewModel.CopyFormat.Tsv);
            e.Handled = true;
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
    // carries no cell of its own), and opens the inspector immediately on a
    // double-click - the discoverable, no-menu path to the same place.
    private void OnResultsGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.Row.DataContext is not object?[] row)
        {
            return;
        }

        _lastPressedRow = row;
        _lastPressedColumnIndex = e.Column.DisplayIndex;

        if (e.PointerPressedEventArgs.ClickCount == 2)
        {
            OpenCellInspector(row, e.Column.DisplayIndex);
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

    private void OpenCellInspector(object?[] row, int columnIndex)
    {
        if (_viewModel is null || _queryViewModel is null || columnIndex >= row.Length
            || columnIndex >= _queryViewModel.ColumnNames.Count)
        {
            return;
        }

        _viewModel.CellInspector.Open(_queryViewModel.ColumnNames[columnIndex], row[columnIndex]);
    }

    private async void OnCellInspectorCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(_viewModel.CellInspector.DisplayText);
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
            _ = browse.SortByAsync(columnName);
        }
    }

    // Keys in the browse filter box: while the column-completion popup is open it
    // owns arrow/Enter/Tab/Esc; otherwise Enter applies the WHERE predicate
    // (re-query from page 1).
    private void OnBrowseFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (FilterCompletionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveFilterCompletionSelection(+1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveFilterCompletionSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Tab:
                    AcceptFilterCompletion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CloseFilterCompletion();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Enter && _queryViewModel?.Browse is { } browse)
        {
            browse.ApplyFilterCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Offers the current dataset's columns as the user types an identifier in the
    // WHERE box — and nothing else (no keywords, functions, or other tables), so
    // the suggestions are exactly the columns a predicate here can reference.
    private void OnBrowseFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressFilterCompletion || _queryViewModel is null)
        {
            return;
        }

        var text = BrowseFilterBox.Text ?? string.Empty;
        var (start, end) = CurrentWordBounds(text, BrowseFilterBox.CaretIndex);
        var word = text[start..end];
        if (word.Length == 0)
        {
            CloseFilterCompletion();
            return;
        }

        var matches = _queryViewModel.ColumnNames
            .Where(c => c.Contains(word, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Nothing worth showing: no match, or the only match is already fully typed.
        if (matches.Count == 0 || (matches.Count == 1 && string.Equals(matches[0], word, StringComparison.OrdinalIgnoreCase)))
        {
            CloseFilterCompletion();
            return;
        }

        FilterCompletionList.ItemsSource = matches;
        FilterCompletionList.SelectedIndex = 0;
        FilterCompletionPopup.IsOpen = true;
    }

    private void OnFilterCompletionTapped(object? sender, TappedEventArgs e) => AcceptFilterCompletion();

    private void MoveFilterCompletionSelection(int delta)
    {
        var count = FilterCompletionList.ItemCount;
        if (count == 0)
        {
            return;
        }

        var index = FilterCompletionList.SelectedIndex + delta;
        FilterCompletionList.SelectedIndex = (index % count + count) % count;
        if (FilterCompletionList.SelectedItem is { } selected)
        {
            FilterCompletionList.ScrollIntoView(selected);
        }
    }

    // Replaces the identifier under the caret with the chosen column (quoted only
    // if Postgres needs it) and drops the popup.
    private void AcceptFilterCompletion()
    {
        if (!FilterCompletionPopup.IsOpen || FilterCompletionList.SelectedItem is not string column)
        {
            return;
        }

        var text = BrowseFilterBox.Text ?? string.Empty;
        var (start, end) = CurrentWordBounds(text, BrowseFilterBox.CaretIndex);
        var insert = SqlIdentifier.QuoteIfNeeded(column);
        var newText = string.Concat(text.AsSpan(0, start), insert, text.AsSpan(end));

        _suppressFilterCompletion = true;
        BrowseFilterBox.Text = newText;
        BrowseFilterBox.CaretIndex = start + insert.Length;
        _suppressFilterCompletion = false;

        CloseFilterCompletion();
    }

    private void CloseFilterCompletion() => FilterCompletionPopup.IsOpen = false;

    // Bounds of the identifier the caret sits in (empty span when not on one).
    private static (int Start, int End) CurrentWordBounds(string text, int caret)
    {
        var end = Math.Clamp(caret, 0, text.Length);
        var start = end;
        while (start > 0 && IsIdentChar(text[start - 1]))
        {
            start--;
        }

        return (start, end);

        static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
    }

    // "Add row…" - opens the insert dialog for the mapped table; on a successful
    // insert the grid refreshes (browse page reload, or a re-run of the query).
    private async void OnAddRowClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _queryViewModel?.EditContext is not { } context)
        {
            return;
        }

        var addRowViewModel = _viewModel.CreateAddRowViewModel(context.Schema, context.Table);
        addRowViewModel.Inserted += () => _ = _queryViewModel.RefreshCurrentAsync();

        var dialog = new AddRowDialog { DataContext = addRowViewModel };
        await dialog.ShowDialog(this);
    }

    private async void OnDeleteRowsClick(object? sender, RoutedEventArgs e) => await DeleteSelectedRowsAsync();

    // Confirms, then deletes the selected rows via primary-key-keyed DELETEs.
    private async Task DeleteSelectedRowsAsync()
    {
        if (_queryViewModel is not { IsEditable: true })
        {
            return;
        }

        var rows = ResultsGrid.SelectedItems.OfType<object?[]>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var noun = rows.Count == 1 ? "this row" : $"these {rows.Count} rows";
        var confirm = new ConfirmDialog($"Delete {noun}? This can't be undone.", "Delete");
        if (await confirm.ShowDialog<bool>(this))
        {
            await _queryViewModel.DeleteRowsAsync(rows);
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

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        var query = _queryViewModel;
        if (query is not null)
        {
            await ExportAsync("csv", "CSV", ["*.csv"], stream => query.ExportCsv(stream));
        }
    }

    private async void OnExportJsonClick(object? sender, RoutedEventArgs e)
    {
        var query = _queryViewModel;
        if (query is not null)
        {
            await ExportAsync("json", "JSON", ["*.json"], stream => query.ExportJson(stream));
        }
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
        write(stream);
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
            return;
        }

        if (e.PropertyName != nameof(QueryViewModel.Sql))
        {
            return;
        }

        if (SqlEditor.Text == _queryViewModel.Sql)
        {
            return;
        }

        _suppressEditorSync = true;
        SqlEditor.Text = _queryViewModel.Sql;
        _suppressEditorSync = false;
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
            ResultsGrid.Columns.Add(new ResultTextColumn(i)
            {
                Header = query.ColumnNames[i],
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
}
