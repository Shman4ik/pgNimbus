using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.Core.Connections;

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
        HintText.Text = $"Neither ~/.ssh/known_hosts nor pgNimbus's own list has a key for {prompt.Server}, so pgNimbus cannot tell whether this is the right server. "
            + "Compare the fingerprint with the one the server's administrator gave you before you accept it.";
        HostText.Text = prompt.Server;
        KeyTypeText.Text = prompt.KeyType;
        FingerprintText.Text = prompt.Fingerprint;
        StoreText.Text = prompt.KnownHostsPath is { } path
            ? $"Accept remembers this key in {path}. If the server later presents a different key, the connection is refused until that line is removed."
            : "Accept trusts this key until pgNimbus closes: there is no app data folder to remember it in, so you will be asked again next time.";
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
