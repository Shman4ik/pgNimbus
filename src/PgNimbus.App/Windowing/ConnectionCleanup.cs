namespace PgNimbus.App;

/// <summary>
/// The connection teardowns of closed main windows that may still be running
/// (<see cref="ViewModels.MainViewModel.CloseConnectionAsync"/>), so the app's
/// exit can give them a bounded moment to finish. When the last window closes
/// the message loop stops right after its <c>Closed</c> handlers, and on macOS
/// <see cref="MacShutdown"/> ends the process from the lifetime's <c>Exit</c>:
/// without this wait a ROLLBACK and a pool's close would be cut off mid-way.
/// (The cancel requests for running queries have gone out by then already.)
/// </summary>
internal static class ConnectionCleanup
{
    /// <summary>How long the exit waits for teardowns that are still running.</summary>
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private static readonly List<Task> Pending = [];

    public static void Track(Task cleanup)
    {
        lock (Pending)
        {
            Pending.RemoveAll(t => t.IsCompleted);
            Pending.Add(cleanup);
        }
    }

    /// <summary>
    /// Blocks until every tracked teardown has finished or <paramref name="timeout"/>
    /// has passed. Safe on the UI thread: the teardowns run on the thread pool.
    /// </summary>
    public static void WaitForPending(TimeSpan timeout)
    {
        Task[] tasks;
        lock (Pending)
        {
            tasks = [.. Pending];
            Pending.Clear();
        }

        try
        {
            Task.WaitAll(tasks, timeout);
        }
        catch
        {
            // A teardown that faulted has nothing more to do.
        }
    }
}
