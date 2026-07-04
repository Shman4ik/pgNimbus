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
/// </summary>
public sealed class NotificationListener : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private NpgsqlConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _listenLoop;

    public event Action<DatabaseNotification>? NotificationReceived;

    public NotificationListener(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public bool IsListening => _connection is not null;

    public async Task StartAsync(IReadOnlyList<string> channels, CancellationToken ct)
    {
        await StopAsync();

        var connection = await _dataSource.OpenConnectionAsync(ct);
        connection.Notification += OnNotification;

        foreach (var channel in channels)
        {
            await using var command = new NpgsqlCommand($"LISTEN {SqlIdentifier.Quote(channel)}", connection);
            await command.ExecuteNonQueryAsync(ct);
        }

        _connection = connection;
        _cts = new CancellationTokenSource();
        _listenLoop = ListenLoopAsync(connection, _cts.Token);
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
        }

        if (_connection is not null)
        {
            _connection.Notification -= OnNotification;
            await _connection.DisposeAsync();
        }

        _connection = null;
        _cts?.Dispose();
        _cts = null;
        _listenLoop = null;
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e) =>
        NotificationReceived?.Invoke(new DatabaseNotification(e.Channel, e.Payload, e.PID, DateTimeOffset.UtcNow));

    private static async Task ListenLoopAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await connection.WaitAsync(ct);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
