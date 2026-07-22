using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>Sidebar panel for the LISTEN/NOTIFY monitor. Binds to a
/// <see cref="NotifyMonitorViewModel"/> supplied as its DataContext by the host window.</summary>
public partial class NotifyMonitorPanel : UserControl
{
    public NotifyMonitorPanel()
    {
        InitializeComponent();
    }

    private void OnRemoveChannelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string channel } && DataContext is NotifyMonitorViewModel vm)
        {
            vm.RemoveChannelCommand.Execute(channel);
        }
    }
}
