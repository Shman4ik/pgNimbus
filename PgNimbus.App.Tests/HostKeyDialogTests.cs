using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Tests;

/// <summary>
/// The prompt for an SSH host key nobody has recorded (security audit
/// 2026-09, finding 4), and the one thing about it that can hang the app:
/// SSH.NET asks from its key-exchange thread and waits for the answer, so the
/// policy blocks that thread on a dialog shown on the UI thread.
/// </summary>
public class HostKeyDialogTests
{
    private static readonly SshHostKeyPrompt Prompt = new(
        "bastion.example.com",
        2222,
        "ssh-ed25519",
        "SHA256:N1SA3QGBX6UpjLt0OmRDWW3oayFMz/ZtKDZsysTgEag",
        "/home/you/.config/pgNimbus/known_hosts");

    /// <summary>
    /// Pumps the UI thread until <paramref name="condition"/> holds, by the
    /// clock rather than by a pass count: the other side is a pool thread, and
    /// on a cold start it can take longer to reach the prompt than any fixed
    /// number of settle passes lasts.
    /// </summary>
    private static bool WaitFor(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                return false;
            }

            Ui.Settle(passes: 1);
            Thread.Sleep(5);
        }

        return true;
    }

    private static HostKeyDialog? WaitForDialog(Window owner)
    {
        HostKeyDialog? dialog = null;
        WaitFor(() => (dialog = owner.OwnedWindows.OfType<HostKeyDialog>().SingleOrDefault()) is not null);
        return dialog;
    }

    [Test]
    public async Task The_dialog_shows_the_host_the_key_type_and_the_fingerprint_ssh_prints()
    {
        await Ui.Run(async () =>
        {
            var dialog = new HostKeyDialog(Prompt);
            Ui.Show(dialog);

            await Assert.That(dialog.FindControl<SelectableTextBlock>("HostText")!.Text).IsEqualTo("bastion.example.com:2222");
            await Assert.That(dialog.FindControl<SelectableTextBlock>("KeyTypeText")!.Text).IsEqualTo("ssh-ed25519");
            await Assert.That(dialog.FindControl<SelectableTextBlock>("FingerprintText")!.Text).IsEqualTo(Prompt.Fingerprint);
            await Assert.That(dialog.FindControl<TextBlock>("StoreText")!.Text).Contains(Prompt.KnownHostsPath);
            await Assert.That(dialog.FindControl<TextBlock>("HintText")!.Text).Contains("bastion.example.com:2222");

            dialog.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task Accept_returns_true()
    {
        await Ui.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);

            var dialog = new HostKeyDialog(Prompt);
            var result = dialog.ShowDialog<bool>(owner);
            Ui.Settle();

            dialog.FindControl<Button>("AcceptButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Ui.Settle();

            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(await result).IsTrue();
            owner.Close();
        });
    }

    /// <summary>
    /// Enter does not accept: the dialog opens about a second after the Enter
    /// that started the connect, and a second reflexive Enter must not vouch
    /// for a key nobody looked at. Escape declines.
    /// </summary>
    [Test]
    public async Task Enter_does_not_accept_and_Escape_declines()
    {
        await Ui.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);

            var dialog = new HostKeyDialog(Prompt);
            var result = dialog.ShowDialog<bool>(owner);
            Ui.Settle();

            await Assert.That(dialog.FindControl<Button>("AcceptButton")!.IsDefault).IsFalse();
            Ui.Press(dialog, Key.Enter);
            await Assert.That(result.IsCompleted).IsFalse();

            Ui.Press(dialog, Key.Escape);
            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(await result).IsFalse();
            owner.Close();
        });
    }

    /// <summary>Closing the window any other way is a no, never a yes.</summary>
    [Test]
    public async Task Closing_the_window_declines()
    {
        await Ui.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);

            var dialog = new HostKeyDialog(Prompt);
            var result = dialog.ShowDialog<bool>(owner);
            Ui.Settle();
            dialog.Close();
            Ui.Settle();

            await Assert.That(await result).IsFalse();
            owner.Close();
        });
    }

    /// <summary>
    /// The round trip the tunnel makes: the policy is called on a pool thread
    /// (as <c>SshTunnel.Connect</c> is, under <c>Task.Run</c>) and blocks it
    /// while the dialog is up on the UI thread, which stays free to pump. The
    /// UI thread here is awaiting that very task, the connection dialog's
    /// shape, and the answer still comes back: no deadlock.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task The_policy_asks_from_a_pool_thread_without_deadlocking_the_UI_thread(bool accept)
    {
        await Ui.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);
            var policy = new HostKeyDialogPolicy(owner);

            var answer = Task.Run(() => policy.AcceptUnknown(Prompt));

            // The dialog appears, owned by the connection window.
            var dialog = WaitForDialog(owner);
            await Assert.That(dialog).IsNotNull();
            await Assert.That(answer.IsCompleted).IsFalse();
            await Assert.That(dialog!.FindControl<SelectableTextBlock>("FingerprintText")!.Text).IsEqualTo(Prompt.Fingerprint);

            if (accept)
            {
                dialog.FindControl<Button>("AcceptButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
            else
            {
                Ui.Press(dialog, Key.Escape);
            }

            await Assert.That(WaitFor(() => answer.IsCompleted)).IsTrue();
            await Assert.That(await answer).IsEqualTo(accept);
            owner.Close();
        });
    }

    /// <summary>Asking from the UI thread would block it on its own dialog; that is refused, not hung.</summary>
    [Test]
    public async Task The_policy_refuses_to_be_asked_from_the_UI_thread()
    {
        await Ui.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);
            var policy = new HostKeyDialogPolicy(owner);

            await Assert.That(Dispatcher.UIThread.CheckAccess()).IsTrue();
            await Assert.That(() => policy.AcceptUnknown(Prompt)).Throws<InvalidOperationException>();
            await Assert.That(owner.OwnedWindows).IsEmpty();
            owner.Close();
        });
    }

    /// <summary>
    /// End to end through the verifier the connection dialog uses: accepting
    /// writes the key to pgNimbus's own file (under the test process's
    /// redirected app data root), and the second check asks nobody.
    /// </summary>
    [Test]
    public async Task An_accepted_key_is_remembered_by_the_app_verifier()
    {
        await Ui.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);

            var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-hostkey-ui-" + Guid.NewGuid().ToString("N"));
            var own = Path.Combine(directory, "known_hosts");
            try
            {
                var verifier = new SshHostKeyVerifier(new HostKeyDialogPolicy(owner), userKnownHostsPath: null, own);
                var key = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIFwcGraO/1L7NHdN9EynwPiJzV1B2bne9G9VZ0KVTdiv");

                var check = Task.Run(() => verifier.Check("bastion", 22, "ssh-ed25519", key));
                var dialog = WaitForDialog(owner);
                await Assert.That(dialog).IsNotNull();
                dialog!.FindControl<Button>("AcceptButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Assert.That(WaitFor(() => check.IsCompleted)).IsTrue();

                await Assert.That(await check).IsNull();
                await Assert.That(File.ReadAllText(own)).Contains("bastion ssh-ed25519 ");

                // Known now: no dialog, answered straight away.
                await Assert.That(await Task.Run(() => verifier.Check("bastion", 22, "ssh-ed25519", key))).IsNull();
                await Assert.That(owner.OwnedWindows).IsEmpty();
            }
            finally
            {
                owner.Close();
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        });
    }
}
