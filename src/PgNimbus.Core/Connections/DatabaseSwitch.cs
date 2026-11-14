using Npgsql;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Connects to another database of the server a window is on, the way that
/// window connected: its profile (or its connection string) with only the
/// database swapped, the same password and the same tunnel endpoint. One real
/// connection is opened before the data source is handed back, so a refusal (no
/// CONNECT privilege, the database dropped meanwhile) throws here, with nothing
/// left open, before the caller changes anything. Used by the title bar's
/// database list and by Open in New Window after a restore.
/// </summary>
public static class DatabaseSwitch
{
    /// <summary>
    /// <paramref name="profile"/> with <paramref name="database"/> in place of its
    /// own. The returned profile is not saved anywhere; <paramref name="profile"/>
    /// itself is a record and is left as it was.
    /// </summary>
    public static async Task<(ConnectionProfile Target, NpgsqlDataSource DataSource)> ConnectAsync(
        ConnectionProfile profile,
        string database,
        string? password,
        (string Host, int Port)? tunnelEndpoint,
        CancellationToken ct = default)
    {
        var target = profile with { Database = database };
        var dataSource = target.CreateDataSource(password, tunnelEndpoint);
        await OpenOnceAsync(dataSource, ct);
        return (target, dataSource);
    }

    /// <summary>
    /// The <c>PGNIMBUS_CONN</c> case: <paramref name="connectionString"/> (as a data
    /// source hands it back, without its password) with the database swapped.
    /// </summary>
    public static async Task<NpgsqlDataSource> ConnectAsync(
        string connectionString,
        string database,
        string? password,
        CancellationToken ct = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = database,
            Password = password,
        };
        var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await OpenOnceAsync(dataSource, ct);
        return dataSource;
    }

    private static async Task OpenOnceAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        try
        {
            await using (await dataSource.OpenConnectionAsync(ct))
            {
            }
        }
        catch
        {
            await dataSource.DisposeAsync();
            throw;
        }
    }
}
