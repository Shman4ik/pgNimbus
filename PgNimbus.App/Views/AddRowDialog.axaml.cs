using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

public partial class AddRowDialog : Window
{
    public AddRowDialog()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is AddRowViewModel vm)
            {
                await vm.LoadAsync();
            }
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
