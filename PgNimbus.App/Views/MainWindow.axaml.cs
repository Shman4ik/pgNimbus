using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private QueryViewModel? _queryViewModel;
    private bool _suppressEditorSync;

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

        SchemaTreeView.DoubleTapped += OnSchemaTreeDoubleTapped;

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
        if (_viewModel is not null && SchemaTreeView.SelectedItem is TableNode table)
        {
            _viewModel.PreviewTable(table);
        }
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
                Binding = new Binding($"[{index}]"),
            });
        }
    }
}
