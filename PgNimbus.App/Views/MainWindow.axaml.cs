using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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

        // TreeViewItem's own DoubleTapped handling (toggling expand) marks the
        // event Handled, so it never reaches a plain `+=` subscription on the
        // parent TreeView. Use AddHandler with handledEventsToo to still see it.
        SchemaTreeView.AddHandler(Gestures.DoubleTappedEvent, OnSchemaTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

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
