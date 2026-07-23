using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
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

/// <summary>
/// The results surface, peeled out of MainWindow (UI design rule 7). It owns the
/// results DataGrid and everything only about it: type-aware column building,
/// inline cell editing + the staged-edit commit path, safe-mode dirty-row washes,
/// follow-FK navigation, copy/export/import, header-click sorting, and the cell
/// inspector (its JSON editor, syntax highlighting, and theme rewrite).
///
/// DataContext is inherited from the host window (a <see cref="MainViewModel"/>):
/// the grid shows whichever tab is active and draws on window-level services (the
/// FK cache, PreviewTableAsync, the CellInspector VM, the Importer,
/// CreateAddRowViewModel), so a per-tab sub-ViewModel wouldn't carry what it needs.
/// The host resolves the panel by name for the few things only it can drive: F6
/// focus hand-off, and the Export/Import command-bar buttons (which live on the
/// toolbar, not this card, and delegate to the public methods here).
/// </summary>
public partial class ResultsGridPanel : UserControl
{
    private MainViewModel? _model;
    // The tab the shared grid currently reflects. Each tab keeps its own
    // Rows/Status; this is re-pointed as MainViewModel.ActiveTab changes.
    private QueryViewModel? _activeQuery;

    // Re-entrancy guard for the cell inspector's JSON editor: the
    // ViewModel↔AvaloniaEdit two-way sync is manual (AvaloniaEdit's Text isn't a
    // bindable AvaloniaProperty).
    private bool _suppressInspectorSync;
    // JSON highlighting for the cell inspector's edit mode (theme-neutral palette).
    private IHighlightingDefinition? _jsonHighlighting;

    private object?[]? _pendingEditRow;
    private int _pendingEditColumnIndex;
    private string? _pendingEditText;
    // The editor's text at the moment editing began, so a commit that ends up
    // equal to it is recognized as "no real change" and skipped (see
    // OnCellEditEnded). Null means the baseline is unknown (skip the guard).
    private string? _pendingEditBaselineText;
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

    public ResultsGridPanel()
    {
        InitializeComponent();

        // Cell-inspector JSON editor: manual two-way sync (AvaloniaEdit's Text
        // isn't a bindable AvaloniaProperty). Editor → ViewModel here; ViewModel
        // → editor in OnCellInspectorPropertyChanged.
        JsonInspectorEditor.TextChanged += (_, _) =>
        {
            if (_model is null || _suppressInspectorSync)
            {
                return;
            }

            _model.CellInspector.EditText = JsonInspectorEditor.Text;
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
        if (this.TryFindResource("AppTextSelectionBrush", out var selectionBrush)
            && selectionBrush is IBrush brush)
        {
            JsonInspectorEditor.TextArea.SelectionBrush = brush;
        }

        ActualThemeVariantChanged += (_, _) => ApplyJsonHighlightingTheme();
        DataContextChanged += OnDataContextChanged;
    }

    // The window's root panel the cell inspector overlay is re-hosted into (see below).
    private Panel? _inspectorOverlayHost;

    // ActualThemeVariant isn't final at construction time; re-resolve the JSON
    // highlighting palette once the panel is in a live visual tree. Also hoist
    // the cell inspector overlay out of this panel's layout so it covers the
    // whole window rather than just the results-pane row it lives in.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyJsonHighlightingTheme();
        HoistCellInspectorToWindowRoot();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Deliberately do NOT remove the hoisted overlay from the window root
        // here. In practice this panel only ever detaches when its window is
        // closing, and mutating the root's Children mid-teardown is a deadlock:
        // the root is itself detaching, so Remove() serializes a compositor
        // change onto a window whose native handle DestroyWindow is already
        // tearing down — the UI thread blocks in DestroyWindow waiting on the
        // render thread, which is waiting to drain that change. (The regression
        // that broke "Switch connection": closing the previous window hung the
        // whole UI. Introduced with the hoist in 3d0b8e0.) Letting the window
        // teardown dispose the overlay with the rest of the tree is exactly the
        // pre-extraction behavior, when the overlay was a static child of
        // MainWindow's root grid. We only drop our claim on it so that if the
        // panel is ever reparented into a live window, HoistCellInspectorToWindowRoot
        // re-hoists (it already reparents the overlay off whatever old parent it
        // still has).
        _inspectorOverlayHost = null;

        base.OnDetachedFromVisualTree(e);
    }

    // The cell inspector is defined inside this panel (its JSON editor, sync, and
    // highlighting all belong here), but the panel sits in the editor/results
    // split's results row, so a child overlay would only dim/center within that
    // row. Reparent it into the window's root Grid — a stretch panel that fills
    // the whole top level — so the scrim dims everything and the card centers in
    // the middle of the window, exactly like the command-palette overlay that is
    // its sibling there. The binding context is unaffected: the root inherits the
    // window's MainViewModel DataContext, so {Binding CellInspector…} resolves as
    // before. Falls back to leaving it in-panel if the host isn't the expected
    // shape (never a crash, just a scoped overlay).
    private void HoistCellInspectorToWindowRoot()
    {
        if (_inspectorOverlayHost is not null
            || TopLevel.GetTopLevel(this) is not Window { Content: Panel root })
        {
            return;
        }

        (CellInspectorOverlay.Parent as Panel)?.Children.Remove(CellInspectorOverlay);
        root.Children.Add(CellInspectorOverlay);
        _inspectorOverlayHost = root;
    }

    // --- Host-driven interactions ----------------------------------------
    // The window resolves the panel by name for these: F6 focus hand-off, and
    // the Export/Import command-bar buttons that delegate here.

    /// <summary>True when keyboard focus is inside the results grid — the host's F6 uses this to hop focus.</summary>
    public bool IsGridFocused => ResultsGrid.IsKeyboardFocusWithin;

    /// <summary>Moves keyboard focus into the results grid.</summary>
    public void FocusGrid() => ResultsGrid.Focus();

    /// <summary>Command-bar "Export → CSV": save the current result set as CSV.</summary>
    public void ExportCsv()
    {
        if (_activeQuery is { } query)
        {
            // Snapshot on the UI thread; ExportAsync runs the returned writer off it.
            _ = ExportAsync("csv", "CSV", ["*.csv"], query.CreateCsvExport());
        }
    }

    /// <summary>Command-bar "Export → JSON": save the current result set as JSON.</summary>
    public void ExportJson()
    {
        if (_activeQuery is { } query)
        {
            _ = ExportAsync("json", "JSON", ["*.json"], query.CreateJsonExport());
        }
    }

    /// <summary>Command-bar "Import": pick a CSV/JSON file and load it into a table.</summary>
    public void Import() => _ = ImportAsync();

    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    // --- ViewModel wiring / active-tab tracking --------------------------

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnMainViewModelPropertyChanged;
            _model.CellInspector.PropertyChanged -= OnCellInspectorPropertyChanged;
        }

        _model = DataContext as MainViewModel;

        if (_model is not null)
        {
            _model.PropertyChanged += OnMainViewModelPropertyChanged;
            _model.CellInspector.PropertyChanged += OnCellInspectorPropertyChanged;
            // Warm the FK cache in the background so the grid's FK-navigation menu
            // items (which can't await) have edges to read by the time it's opened.
            _ = _model.EnsureForeignKeysAsync();
            AttachQuery(_model.ActiveTab);
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ActiveTab is transiently null while the tab ListBox reacts to the
        // removal of its selected item (see MainViewModel.CloseTab).
        if (e.PropertyName == nameof(MainViewModel.ActiveTab) && _model is { ActiveTab: not null })
        {
            AttachQuery(_model.ActiveTab);
        }
    }

    // Switching the active tab swaps which QueryViewModel the shared results grid
    // reflects - each tab keeps its own Rows/Status, but there's only one
    // on-screen grid, so this re-points it at the new tab. (The editor tracks the
    // active tab independently, inside QueryEditorPanel.)
    private void AttachQuery(QueryViewModel? query)
    {
        if (_activeQuery is not null)
        {
            _activeQuery.PropertyChanged -= OnActiveQueryPropertyChanged;
            _activeQuery.ColumnNames.CollectionChanged -= OnColumnNamesChanged;
        }

        _activeQuery = query;
        if (_activeQuery is null)
        {
            return;
        }

        _activeQuery.PropertyChanged += OnActiveQueryPropertyChanged;
        _activeQuery.ColumnNames.CollectionChanged += OnColumnNamesChanged;

        ResultsGrid.ItemsSource = _activeQuery.Rows;
        RebuildColumns(_activeQuery);
        // The new tab's staged set (if any) tints different rows than the old
        // tab's — repaint once its rows have realized.
        Dispatcher.UIThread.Post(RefreshPendingRowHighlights, DispatcherPriority.Background);
    }

    private void OnColumnNamesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns(_activeQuery!);

    private void OnActiveQueryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_activeQuery is null)
        {
            return;
        }

        // Rows is swapped wholesale instead of mutated in place (bulk
        // collection-change handling in the DataGrid costs ~200 µs/row; a
        // fresh ItemsSource costs a viewport) - re-point the grid each time.
        if (e.PropertyName == nameof(QueryViewModel.Rows))
        {
            ResultsGrid.ItemsSource = _activeQuery.Rows;
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
            RebuildColumns(_activeQuery);
        }
    }

    // --- Cell inspector: JSON editor highlighting + sync -----------------

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
        var json = _model?.CellInspector.IsJson == true ? _jsonHighlighting : null;
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

        if (e.PropertyName != nameof(CellInspectorViewModel.EditText) || _model is null)
        {
            return;
        }

        var text = _model.CellInspector.EditText;
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

    // --- Inline cell editing ---------------------------------------------

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

        if (_activeQuery is null || row is null || text is null || e.EditAction != DataGridEditAction.Commit)
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

        await _activeQuery.CommitCellEditAsync(row, columnIndex, text);
    }

    // --- Safe mode: dirty-row highlighting ---------------------------------

    // Translucent washes so grid lines and the selection state stay readable
    // in both themes: amber = staged edit, red = staged delete.
    private static readonly IBrush StagedEditRowBrush = new SolidColorBrush(Color.Parse("#38D9822B"));
    private static readonly IBrush StagedDeleteRowBrush = new SolidColorBrush(Color.Parse("#38E03131"));

    private void ApplyRowStaging(DataGridRow row)
    {
        var staging = _activeQuery is { } query && row.DataContext is object?[] values
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

    // --- Key handling ------------------------------------------------------

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
        if (e.Key == Key.Delete && _activeQuery is { IsEditable: true } && !ResultsGrid.IsReadOnly)
        {
            if (ResultsGrid.SelectedItems.OfType<object?[]>().Any())
            {
                _ = DeleteSelectedRowsAsync();
                e.Handled = true;
            }
        }
    }

    // --- Pointer / double-click ------------------------------------------

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
        if (_activeQuery is not { } query || columnIndex >= query.ColumnNames.Count)
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
        if (_activeQuery is { } vm && _lastPressedRow is { } row)
        {
            _ = vm.SetCellNullAsync(row, _lastPressedColumnIndex);
        }
    }

    // --- Follow a foreign key from the grid --------------------------------

    // The forward hop staged by the last menu-opening pass, consumed by
    // OnFollowFkClick. (Reverse hops are captured per sub-item closure.)
    private ForeignKeyHop? _followHop;
    // Resolved lazily from the menu's items: named elements inside a
    // ContextMenu aren't reliably reachable through the control's name scope.
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

        if (_model is not { } vm || _activeQuery is not { } query || _lastPressedRow is null)
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
        if (_model is not { } vm || _activeQuery is not { } query || _lastPressedRow is not { } row)
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

    // --- Cell inspector ----------------------------------------------------

    private void OpenCellInspector(object?[] row, int columnIndex, bool startEditing = false)
    {
        if (_model is null || _activeQuery is null || columnIndex >= row.Length
            || columnIndex >= _activeQuery.ColumnNames.Count)
        {
            return;
        }

        var query = _activeQuery;
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
        _model.CellInspector.Open(name, row[columnIndex], columnIndex, canEdit, commit, validatesAsJson, startEditing && canEdit);
    }

    private async void OnCellInspectorCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_model is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_model.CellInspector.DisplayText);
        }
        catch
        {
            // Clipboard access can throw if another app holds it locked. This is
            // an async void handler, so an unhandled throw would crash the app —
            // a failed copy is not worth that.
        }
    }

    private void OnCellInspectorScrimPressed(object? sender, PointerPressedEventArgs e) =>
        _model?.CellInspector.CloseCommand.Execute(null);

    // Swallow presses on the card so they don't bubble to the scrim and close it.
    private void OnCellInspectorCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // --- Sorting -----------------------------------------------------------

    // In browse mode a header click sorts server-side (ORDER BY + reload page 1)
    // instead of the client-side comparer sort - cancel the default and re-query.
    private void OnResultsGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_activeQuery?.Browse is { } browse && e.Column.Header is string columnName)
        {
            e.Handled = true;
            // A header click re-queries page 1; ignore it while a run is already
            // in flight so it can't start a second concurrent execution.
            if (!_activeQuery.IsRunning)
            {
                _ = browse.SortByAsync(columnName);
            }
        }
    }

    // --- Row insert / delete ----------------------------------------------

    // "Add row…" - opens the insert dialog for the mapped table; on a successful
    // insert the grid refreshes (browse page reload, or a re-run of the query).
    // In safe mode the dialog stages the INSERT into the tab's pending set
    // instead of executing it.
    private async void OnAddRowClick(object? sender, RoutedEventArgs e)
    {
        if (_model is null || _activeQuery is not { EditContext: { } context } query || HostWindow is not { } owner)
        {
            return;
        }

        var addRowViewModel = _model.CreateAddRowViewModel(
            context.Schema,
            context.Table,
            query.ShouldStageChanges ? query.TryStageInsert : null);
        addRowViewModel.Inserted += () => _ = query.RefreshCurrentAsync();

        var dialog = new AddRowDialog { DataContext = addRowViewModel };
        await dialog.ShowDialog(owner);
    }

    private async void OnDeleteRowsClick(object? sender, RoutedEventArgs e) => await DeleteSelectedRowsAsync();

    // Confirms, then deletes the selected rows via primary-key-keyed DELETEs.
    // In safe mode there's nothing to confirm — the delete is only staged
    // (and Delete on an already-staged row unstages it), reversible until
    // the set is committed.
    private async Task DeleteSelectedRowsAsync()
    {
        if (_activeQuery is not { IsEditable: true } query)
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

        if (HostWindow is not { } owner)
        {
            return;
        }

        var noun = rows.Count == 1 ? "this row" : $"these {rows.Count} rows";
        var confirm = new ConfirmDialog($"Delete {noun}? This can't be undone.", "Delete");
        if (await confirm.ShowDialog<bool>(owner))
        {
            await query.DeleteRowsAsync(rows);
        }
    }

    // --- Copy --------------------------------------------------------------

    private void OnCopyCells(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Tsv);

    private void OnCopyAsCsv(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Csv);

    private void OnCopyAsJson(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Json);

    private void OnCopyAsMarkdown(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Markdown);

    private void OnCopyAsInsert(object? sender, RoutedEventArgs e) => _ = CopySelectionAsync(QueryViewModel.CopyFormat.Insert);

    // Copies the selected rows (or the whole result set when nothing is selected)
    // in the chosen shape.
    private async Task CopySelectionAsync(QueryViewModel.CopyFormat format)
    {
        if (_activeQuery is null)
        {
            return;
        }

        var selected = ResultsGrid.SelectedItems.OfType<object?[]>().ToList();
        var text = _activeQuery.CopyRows(format, selected);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    // --- Export / import ---------------------------------------------------

    // "Import" on the command bar: pick a CSV/JSON file, parse it, and hand a
    // prefilled target-table dialog to the user. On success the schema tree
    // refreshes and the active tab SELECTs the fresh table as visible proof.
    private async Task ImportAsync()
    {
        if (_model is null || HostWindow is not { } owner)
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
                _model.ActiveTab.Status = "Nothing to import — the file has no rows.";
                return;
            }

            var schemas = _model.SchemaTree.Schemas.OfType<SchemaNode>().Select(s => s.Name).ToList();
            var importViewModel = new ImportViewModel(_model.Importer, data, SuggestTableName(files[0].Name), schemas);
            importViewModel.Completed += (schema, table, count) => _ = OnImportCompletedAsync(schema, table, count);

            var dialog = new ImportDialog { DataContext = importViewModel };
            await dialog.ShowDialog<bool>(owner);
        }
        catch (Exception ex)
        {
            _model.ActiveTab.Status = $"Import failed: {ex.Message}";
            _model.ActiveTab.HasError = true;
        }
    }

    private async Task OnImportCompletedAsync(string schema, string table, long count)
    {
        if (_model is null)
        {
            return;
        }

        await _model.RefreshSchemaCommand.ExecuteAsync(null);

        var tab = _model.ActiveTab;
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
        if (write is null || _activeQuery is null || _activeQuery.Rows.Count == 0)
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

    // --- Plan copy / export ------------------------------------------------
    // Share the current query plan into external tools (pev2, depesz, an issue).
    // JSON is the interchange format; the rendered text is the human-readable one.
    // The JSON actions are hidden (see the flyout) when the plan came from a text
    // import, which has no JSON to hand out.

    private void OnCopyPlanJson(object? sender, RoutedEventArgs e) => _ = CopyToClipboardAsync(_activeQuery?.PlanJson);

    private void OnCopyPlanText(object? sender, RoutedEventArgs e) => _ = CopyToClipboardAsync(_activeQuery?.ExplainText);

    private void OnSavePlanJson(object? sender, RoutedEventArgs e) =>
        _ = SavePlanAsync(_activeQuery?.PlanJson, "json", "JSON", ["*.json"]);

    private void OnSavePlanText(object? sender, RoutedEventArgs e) =>
        _ = SavePlanAsync(_activeQuery?.ExplainText, "txt", "Text", ["*.txt"]);

    private async Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        if (_activeQuery is not null)
        {
            _activeQuery.Status = "Plan copied to clipboard";
        }
    }

    private async Task SavePlanAsync(string? content, string extension, string typeName, string[] patterns)
    {
        if (string.IsNullOrEmpty(content) || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save query plan",
            SuggestedFileName = $"plan.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = patterns }],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content);
            if (_activeQuery is not null)
            {
                _activeQuery.Status = $"Plan saved to {file.Name}";
            }
        }
        catch (Exception ex) when (_activeQuery is not null)
        {
            _activeQuery.Status = $"Save failed: {ex.Message}";
            _activeQuery.HasError = true;
        }
    }

    // --- Column building ---------------------------------------------------

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
