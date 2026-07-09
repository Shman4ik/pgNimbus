using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    // Reads the profile off the tapped ListBoxItem's DataContext rather than
    // the ListBox's SelectedItem: on the very first click of a row that
    // wasn't already selected, SelectedItem can still be stale/null at the
    // point this handler runs.
    private void OnProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container?.DataContext is ConnectionProfile profile && DataContext is ConnectionDialogViewModel vm)
        {
            vm.SelectedProfile = profile;
            vm.ConnectCommand.Execute(null);
        }
    }
}
