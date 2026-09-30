using Npgsql;
using PgNimbus.Core.Notifications;

namespace PgNimbus.Core.Tests.Notifications;

/// <summary>
/// The listener against a real server: that a NOTIFY published from here comes
/// back, and — the reason this class exists — that killing the backend it is
/// parked on does not silently end the monitoring. Before the reconnect, a
/// dropped connection faulted the wait loop, nothing observed the exception,
/// and the UI went on reporting "Listening on N channels" over a listener that
/// no longer existed.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> exactly like
/// <see cref="Query.QueryEngineReconnectTests"/>: unset (a plain local run),
/// every test here skips cleanly; CI's <c>postgres:17</c> service container
/// sets it.
/// </summary>
// Kills every other backend on the test database, so it must not run beside
// anything else pointed at the same one.
[NotInParallel]
public class NotificationListenerTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to test LISTEN/NOTIFY against.");
        }
    }

    private static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString!);

    private const string Channel = "pgnimbus_test_channel";

    [Test]
    [Timeout(30_000)]
    public async Task NotificationsArriveOnASubscribedChannel(CancellationToken ct)
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        await using var listener = new NotificationListener(dataSource);

        var received = new TaskCompletionSource<DatabaseNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.NotificationReceived += n => received.TrySetResult(n);

        await listener.StartAsync([Channel], ct);
        await listener.SendAsync(Channel, """{"event":"order.paid"}""", ct);

        var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        await Assert.That(notification.Channel).IsEqualTo(Channel);
        await Assert.That(notification.Payload).IsEqualTo("""{"event":"order.paid"}""");
        await Assert.That(notification.ProcessId).IsGreaterThan(0);
    }

    /// <summary>
    /// The listening connection is killed server-side, the way a dropped tunnel
    /// or a sleeping laptop kills it. The listener has to re-establish it, get
    /// back on every channel, and go on delivering — <see cref="NotificationListener.IsListening"/>
    /// staying true is the part the UI reads.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task ADroppedConnectionIsReestablishedAndStillDelivers(CancellationToken ct)
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        await using var listener = new NotificationListener(dataSource);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Reconnected += () => reconnected.TrySetResult();

        var stopped = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Stopped += ex => stopped.TrySetResult(ex);

        await listener.StartAsync([Channel], ct);
        await KillBackendsAsync(ct);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await Assert.That(listener.IsListening).IsTrue();
        await Assert.That(stopped.Task.IsCompleted).IsFalse();

        // Re-subscribed, not merely reconnected: a fresh connection that never
        // re-issued LISTEN would sit there receiving nothing.
        var received = new TaskCompletionSource<DatabaseNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.NotificationReceived += n => received.TrySetResult(n);
        await listener.SendAsync(Channel, "after-the-drop", ct);

        var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Assert.That(notification.Payload).IsEqualTo("after-the-drop");
    }

    /// <summary>
    /// Stopping is not a failure: no <see cref="NotificationListener.Stopped"/>,
    /// and the connection is handed back rather than left parked in a wait.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task StoppingIsNotReportedAsAFailure(CancellationToken ct)
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        await using var listener = new NotificationListener(dataSource);

        var stopped = false;
        listener.Stopped += _ => stopped = true;

        await listener.StartAsync([Channel], ct);
        await listener.StopAsync();

        await Assert.That(listener.IsListening).IsFalse();
        await Assert.That(stopped).IsFalse();
    }

    // Same recipe as the query engine's reconnect tests: kill from a separate
    // admin connection so the killer is not one of the killed, then give
    // Postgres a moment — pg_terminate_backend signals the backend, it does not
    // block until the socket is gone.
    private static async Task KillBackendsAsync(CancellationToken ct)
    {
        await using var admin = CreateDataSource();
        await using var connection = await admin.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
              FROM pg_stat_activity
             WHERE pid <> pg_backend_pid()
               AND datname = current_database()
            """,
            connection);
        await command.ExecuteNonQueryAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
    }
}
