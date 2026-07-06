using System.Collections.Specialized;
using System.ComponentModel;
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
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using PgNimbus.App.ViewModels;

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
    private ShortcutsWindow? _shortcutsWindow;
    private IHighlightingDefinition? _sqlHighlighting;

    public MainWindow()
    {
        InitializeComponent();

        LoadSqlHighlighting();
        // ActualThemeVariant isn't final at construction time - re-resolve the
        // palette (and the toggle glyph) once the window opens, and again on
        // any live theme switch, however it originates.
        Opened += (_, _) =>
        {
            ApplySqlHighlightingTheme();
            UpdateThemeIcon();
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplySqlHighlightingTheme();
            UpdateThemeIcon();
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
        ResultsGrid.CellEditEnding += OnCellEditEnding;
        ResultsGrid.CellEditEnded += OnCellEditEnded;
        ResultsGrid.PreparingCellForEdit += OnPreparingCellForEdit;
        ResultsGrid.KeyDown += OnResultsGridKeyDown;

        SqlEditor.TextArea.TextEntered += OnSqlTextEntered;
        SqlEditor.KeyDown += OnSqlEditorKeyDown;

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
        }

        _viewModel = vm;
        _viewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        // Palette actions that touch the window are handled here.
        _viewModel.ThemeToggleRequested += ToggleTheme;
        _viewModel.ShortcutsRequested += ShowShortcutsWindow;

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

    private void OnShowShortcutsClick(object? sender, RoutedEventArgs e) => ShowShortcutsWindow();

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
        if (string.IsNullOrEmpty(e.Text) || _completionWindow is not null)
        {
            return;
        }

        var c = e.Text[0];
        if (char.IsLetter(c) || c == '_')
        {
            ShowCompletion(includeTypedChar: true);
        }
    }

    private void OnSqlEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            ShowCompletion(includeTypedChar: false);
            e.Handled = true;
        }
    }

    private void ShowCompletion(bool includeTypedChar)
    {
        var data = _viewModel?.CompletionProvider.GetCompletionData();
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

    // Ctrl+C copies the selection as TSV (spreadsheet-friendly). ClipboardCopyMode
    // is None on the grid because our columns bind through a converter with no
    // path, so the stock copy has no cell text to read - we build it ourselves.
    private void OnResultsGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopySelectionAsync(QueryViewModel.CopyFormat.Tsv);
            e.Handled = true;
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
            });
        }
    }
}
