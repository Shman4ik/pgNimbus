using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

public partial class MainWindow : Window
{
    private QueryViewModel? _viewModel;
    private bool _suppressEditorSync;

    public MainWindow()
    {
        InitializeComponent();

        SqlEditor.TextChanged += (_, _) =>
        {
            if (_viewModel is null || _suppressEditorSync)
            {
                return;
            }

            _viewModel.Sql = SqlEditor.Text;
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is QueryViewModel vm)
            {
                Attach(vm);
            }
        };
    }

    private void Attach(QueryViewModel vm)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = vm;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SqlEditor.Text = vm.Sql;
        ResultsGrid.ItemsSource = vm.Rows;
        RebuildColumns(vm);

        vm.ColumnNames.CollectionChanged += (_, _) => RebuildColumns(vm);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueryViewModel.Sql) || _viewModel is null)
        {
            return;
        }

        if (SqlEditor.Text == _viewModel.Sql)
        {
            return;
        }

        _suppressEditorSync = true;
        SqlEditor.Text = _viewModel.Sql;
        _suppressEditorSync = false;
    }

    private void RebuildColumns(QueryViewModel vm)
    {
        ResultsGrid.Columns.Clear();

        for (var i = 0; i < vm.ColumnNames.Count; i++)
        {
            var index = i;
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = vm.ColumnNames[index],
                Binding = new Binding($"[{index}]"),
            });
        }
    }
}
