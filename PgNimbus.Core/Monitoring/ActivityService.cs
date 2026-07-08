using Npgsql;

namespace PgNimbus.Core.Monitoring;

/// <summary>One client backend from pg_stat_activity. <paramref name="ElapsedSeconds"/> counts from query_start.</summary>
public sealed record BackendActivity(
    int Pid,
    string? User,
    string? Database,
    string? Application,
    string? ClientAddress,
    string State,
    string? WaitEventType,
    string? WaitEvent,
    double ElapsedSeconds,
    string Query)
{
    /// <summary>True when the backend is stuck waiting on a lock — the row the activity view highlights.</summary>
    public bool IsWaitingOnLock => WaitEventType == "Lock";
}

/// <summary>
/// Live server activity: pg_stat_activity snapshots plus the two backend
/// controls (cancel the running statement / terminate the whole backend).
/// </summary>
public sealed class ActivityService
{
    private readonly NpgsqlDataSource _dataSource;

    public ActivityService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>Client backends other than our own, active ones first.</summary>
    public async Task<IReadOnlyList<BackendActivity>> GetActivityAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT a.pid,
                   a.usename,
                   a.datname,
                   a.application_name,
                   a.client_addr::text,
                   COALESCE(a.state, ''),
                   a.wait_event_type,
                   a.wait_event,
                   COALESCE(EXTRACT(EPOCH FROM (now() - a.query_start))::float8, 0),
                   COALESCE(a.query, '')
            FROM pg_catalog.pg_stat_activity a
            WHERE a.backend_type = 'client backend'
              AND a.pid <> pg_backend_pid()
            ORDER BY (a.state = 'active') DESC, a.query_start NULLS LAST
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<BackendActivity>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BackendActivity(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetDouble(8),
                reader.GetString(9)));
        }

        return results;
    }

    /// <summary>pg_cancel_backend — stops the running statement, keeps the session. False if the pid was already gone.</summary>
    public Task<bool> CancelBackendAsync(int pid, CancellationToken ct) =>
        SignalBackendAsync("SELECT pg_catalog.pg_cancel_backend(@pid)", pid, ct);

    /// <summary>pg_terminate_backend — kills the whole session. False if the pid was already gone.</summary>
    public Task<bool> TerminateBackendAsync(int pid, CancellationToken ct) =>
        SignalBackendAsync("SELECT pg_catalog.pg_terminate_backend(@pid)", pid, ct);

    private async Task<bool> SignalBackendAsync(string sql, int pid, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("pid", pid);
        return await command.ExecuteScalarAsync(ct) is true;
    }
}
