using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;
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

    public MainWindow()
    {
        InitializeComponent();

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

    private void Attach(MainViewModel vm)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        }

        _viewModel = vm;
        _viewModel.PropertyChanged += OnMainViewModelPropertyChanged;

        AttachQuery(vm.ActiveTab);
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab) && _viewModel is not null)
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

    private void RebuildColumns(QueryViewModel query)
    {
        ResultsGrid.Columns.Clear();

        for (var i = 0; i < query.ColumnNames.Count; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
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
