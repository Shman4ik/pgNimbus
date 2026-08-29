using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The LISTEN/NOTIFY monitor: channel subscriptions, the live feed, one
/// payload in detail, and a send box. A window like the other two reference
/// views, because it is watched *while* the application under test runs — the
/// thing an overlay over the shell cannot be.
/// </summary>
public partial class NotifyMonitorWindow : Window
{
    public NotifyMonitorWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    // The ✕ on a channel row: the row's DataContext is the channel string
    // itself, so the button carries it on Tag rather than binding a command
    // parameter through the item template's own DataContext.
    private void OnRemoveChannelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string channel } && DataContext is NotifyMonitorViewModel vm)
        {
            vm.RemoveChannelCommand.Execute(channel);
        }
    }

    private async void OnCopyPayloadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NotifyMonitorViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(vm.Payload.DisplayText);
        }
        catch
        {
            // Losing a clipboard write (another app holding it open, a locked
            // session) is not worth an error dialog over a copy button.
        }
    }
}
