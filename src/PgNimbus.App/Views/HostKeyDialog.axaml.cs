using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Views;

/// <summary>
/// Asks whether to trust an SSH host key that neither <c>~/.ssh/known_hosts</c>
/// nor pgNimbus's own list knows. Shown via <c>ShowDialog&lt;bool&gt;</c>:
/// true accepts (the tunnel then records the key), false or closing the
/// window declines and the connect fails. Self-contained, no view model: it
/// is opened from a thread-pool thread in the middle of SSH.NET's key
/// exchange (see <see cref="HostKeyDialogPolicy"/>) and holds nothing but
/// the prompt's text.
/// </summary>
public partial class HostKeyDialog : Window
{
    public HostKeyDialog()
    {
        InitializeComponent();
        DialogChrome.Attach(this);
    }

    public HostKeyDialog(SshHostKeyPrompt prompt) : this()
    {
        HintText.Bind(TextBlock.TextProperty, Localizer.Observe(() => Strings.Dialogs_HostKeyHint(prompt.Server)).ToBinding());
        HostText.Text = prompt.Server;
        KeyTypeText.Text = prompt.KeyType;
        FingerprintText.Text = prompt.Fingerprint;
        StoreText.Bind(TextBlock.TextProperty, Localizer.Observe(() => prompt.KnownHostsPath is { } path
            ? Strings.Dialogs_HostKeyRemembered(path)
            : Strings.Dialogs_HostKeySession).ToBinding());
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
