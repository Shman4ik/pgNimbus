using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>CSV/JSON import target picker. Closes itself (returning true) once the ViewModel reports a successful load.</summary>
public partial class ImportDialog : Window
{
    public ImportDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImportViewModel vm)
            {
                vm.Completed += (_, _, _) => Close(true);
            }
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
