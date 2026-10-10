using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The install steps shown when pg_dump or pg_restore can't be found, in the
/// backup window and on the Settings page. Copying a command and opening a
/// page are the view's: they need the window's clipboard and launcher.
/// </summary>
public partial class PgToolGuideView : UserControl
{
    public PgToolGuideView()
    {
        InitializeComponent();
    }

    private async void OnCopyCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PgToolGuideStepViewModel { Command: { } command } } button
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(command);
            CopyFeedback.Show(button);
        }
        catch (Exception)
        {
            // The command is also selectable text; a clipboard that refused
            // leaves that way open.
        }
    }

    private async void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PgToolGuideStepViewModel { LinkUrl: { } url } }
            || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            await launcher.LaunchUriAsync(uri);
        }
        catch (Exception)
        {
            // The address is in the button's tooltip.
        }
    }
}
