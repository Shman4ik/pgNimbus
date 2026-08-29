using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Notifications;

public sealed record DatabaseNotification(string Channel, string Payload, int ProcessId, DateTimeOffset ReceivedAt);

/// <summary>
/// Keeps a dedicated connection open, `LISTEN`ing on a set of channels, and
/// surfaces every `NOTIFY` as an event. Npgsql only delivers notifications
/// while something is actively waiting on the connection, so a background
/// loop repeatedly calls <see cref="NpgsqlConnection.WaitAsync"/> for as long
/// as listening is active.
///
/// A monitor that has silently stopped monitoring is worse than no monitor, so
/// the loop does not merely die when its connection drops: a loss (classified
/// by the same <see cref="ConnectionFailure"/> the query engine uses) is
/// retried once on a fresh connection with every channel re-subscribed, and
/// only a second failure gives up — reported through <see cref="Stopped"/>, so
/// the UI can stop claiming to be listening. Anything that is not a connection
/// loss stops immediately: re-running it would fail the same way.
/// </summary>
public sealed class NotificationListener : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private NpgsqlConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _listenLoop;
    private IReadOnlyList<string> _channels = [];

    public event Action<DatabaseNotification>? NotificationReceived;

    /// <summary>
    /// Raised when listening ended on its own: the connection dropped and could
    /// not be re-established, or failed in a way retrying cannot fix. Carries the
    /// failure so the caller can say what happened. Never raised by
    /// <see cref="StopAsync"/> — that is the user stopping, not a failure.
    /// Raised from the listener's background loop, not the caller's thread.
    /// </summary>
    public event Action<Exception>? Stopped;

    /// <summary>
    /// Raised after a dropped connection was re-established and every channel
    /// re-subscribed. Nothing published while the connection was down arrives:
    /// NOTIFY keeps no backlog for a listener that is not connected. So this is
    /// a "you may have missed some" signal as much as a recovery one. Raised
    /// from the listener's background loop, not the caller's thread.
    /// </summary>
    public event Action? Reconnected;

    public NotificationListener(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public bool IsListening => _connection is not null;

    public async Task StartAsync(IReadOnlyList<string> channels, CancellationToken ct)
    {
        await StopAsync();

        _channels = channels.ToList();
        _connection = await OpenAndSubscribeAsync(ct);
        _cts = new CancellationTokenSource();
        _listenLoop = ListenLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_listenLoop is not null)
        {
            try
            {
                await _listenLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the wait loop on stop.
            }
            catch
            {
                // The loop faulted and has already reported itself through
                // Stopped. Swallow it so the cleanup below still runs —
                // rethrowing would leave the listener half-stopped with the
                // connection never disposed.
            }
        }

        await DisposeConnectionAsync();

        _cts?.Dispose();
        _cts = null;
        _listenLoop = null;
    }

    /// <summary>
    /// Publishes a notification, so the monitor can be exercised without a
    /// second session open somewhere else. Goes through <c>pg_notify</c> rather
    /// than the <c>NOTIFY</c> statement because only the function form takes its
    /// channel and payload as parameters — <c>NOTIFY</c> wants literals, which
    /// would mean splicing user-typed text into a statement by hand. Sent on a
    /// pooled connection, never the listening one, which is parked inside a
    /// <c>WaitAsync</c>.
    /// </summary>
    public async Task SendAsync(string channel, string payload, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(ct);
    }

    // Opens a connection and LISTENs on every channel. On failure the connection
    // is disposed here: it has not been handed to _connection yet, so nothing
    // else would ever hand it back to the pool.
    private async Task<NpgsqlConnection> OpenAndSubscribeAsync(CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            connection.Notification += OnNotification;

            foreach (var channel in _channels)
            {
                await using var command = new NpgsqlCommand($"LISTEN {SqlIdentifier.Quote(channel)}", connection);
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            connection.Notification -= OnNotification;
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    private async Task DisposeConnectionAsync()
    {
        var connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            connection.Notification -= OnNotification;
            await connection.DisposeAsync();
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e) =>
        NotificationReceived?.Invoke(new DatabaseNotification(e.Channel, e.Payload, e.PID, DateTimeOffset.Now));

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        // Whether the current connection is a replacement whose health is not
        // proven yet. One retry per drop, so a server that accepts a connection
        // and immediately closes it cannot spin this loop.
        var retried = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // The connection is only ever replaced from inside this loop, so
                // reading the field here always sees the live one.
                await _connection!.WaitAsync(ct);

                // A wait that returned without throwing proves the connection
                // works, so the next drop gets its own retry.
                retried = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (retried || !ConnectionFailure.IsLoss(ex) || !await TryReconnectAsync(ct))
                {
                    await DisposeConnectionAsync();
                    Stopped?.Invoke(ex);
                    return;
                }

                retried = true;
                Reconnected?.Invoke();
            }
        }
    }

    // Re-establishes the listening connection after a loss: drop the corpse,
    // flush the pool (every connection that sat idle through the same tunnel
    // drop or laptop sleep is equally dead, so renting one back would fail the
    // same way), and re-LISTEN on a fresh one.
    private async Task<bool> TryReconnectAsync(CancellationToken ct)
    {
        try
        {
            await DisposeConnectionAsync();
        }
        catch
        {
            // Disposing a connection whose socket is already gone can throw; it
            // is being abandoned either way, and DisposeConnectionAsync has
            // already cleared the field.
        }

        try
        {
            _dataSource.Clear();
            _connection = await OpenAndSubscribeAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
