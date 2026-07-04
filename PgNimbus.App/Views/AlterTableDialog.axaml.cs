using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

public partial class AlterTableDialog : Window
{
    public AlterTableDialog()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is AlterTableViewModel vm)
            {
                await vm.LoadAsync();
            }
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
