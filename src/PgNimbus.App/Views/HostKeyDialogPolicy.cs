using Avalonia.Controls;
using Avalonia.Threading;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Views;

/// <summary>
/// The App's <see cref="ISshHostKeyPolicy"/>: an unknown host key is put to
/// the user in a <see cref="HostKeyDialog"/> owned by the connection dialog.
/// <para>
/// SSH.NET raises <c>HostKeyReceived</c> synchronously on the thread running
/// the key exchange and waits for <c>CanTrust</c>, so the answer has to come
/// back before the event handler returns. The connection dialog runs
/// <see cref="SshTunnel.Connect"/> on a thread-pool thread (<c>Task.Run</c>)
/// and <em>awaits</em> it, which leaves the UI thread free to pump: this posts
/// the dialog to the UI thread and blocks the pool thread until it closes.
/// That cannot deadlock as long as the caller is not the UI thread, which is
/// why the one thing it refuses is being called from there
/// (<c>HostKeyDialogTests</c> drives the whole round trip headlessly).
/// </para>
/// </summary>
public sealed class HostKeyDialogPolicy(Window owner) : ISshHostKeyPolicy
{
    public bool AcceptUnknown(SshHostKeyPrompt prompt)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            // Blocking the UI thread on its own dialog would hang the app; a
            // caller on this thread is a programming error, not a user's problem.
            throw new InvalidOperationException("The SSH host key prompt must be asked from off the UI thread; SshTunnel.Connect belongs on a Task.Run.");
        }

        return Dispatcher.UIThread
            .InvokeAsync(() => new HostKeyDialog(prompt).ShowDialog<bool>(owner))
            .GetAwaiter()
            .GetResult();
    }
}
