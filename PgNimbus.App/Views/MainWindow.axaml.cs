using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
        SchemaTreeView.AddHandler(Gestures.DoubleTappedEvent, OnSchemaTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        ResultsGrid.CellEditEnding += OnCellEditEnding;
        ResultsGrid.CellEditEnded += OnCellEditEnded;

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
        if (_queryViewModel is not null)
        {
            _queryViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = vm;
        _queryViewModel = vm.Query;
        _queryViewModel.PropertyChanged += OnViewModelPropertyChanged;

        SqlEditor.Text = vm.Query.Sql;
        ResultsGrid.ItemsSource = vm.Query.Rows;
        RebuildColumns(vm.Query);

        vm.Query.ColumnNames.CollectionChanged += (_, _) => RebuildColumns(vm.Query);
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
        if (e.PropertyName != nameof(QueryViewModel.Sql) || _queryViewModel is null)
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
            var index = i;
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = query.ColumnNames[index],
                Binding = new Binding($"[{index}]") { Mode = BindingMode.OneWay },
            });
        }
    }
}
