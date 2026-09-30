using Npgsql;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Probes a connection string by opening (and immediately closing) a real
/// connection. Pooling is forced off so a successful test doesn't leave an
/// idle pooled connection behind on the server.
/// </summary>
public static class ConnectionTester
{
    /// <summary>Opens the connection and returns the server version it reports; throws on failure.</summary>
    public static async Task<string> TestAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection.ServerVersion;
    }

    /// <summary>
    /// Probes a profile the way a connect opens it (<see cref="ConnectionProfile.CreateDataSource"/>),
    /// so a tunnelled test checks the certificate against the real host just as
    /// the connect will. Unpooled, like the string overload.
    /// </summary>
    public static async Task<string> TestAsync(
        ConnectionProfile profile,
        string? password,
        (string Host, int Port)? endpointOverride = null,
        CancellationToken cancellationToken = default)
    {
        await using var dataSource = profile.CreateDataSource(password, endpointOverride, pooling: false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return connection.ServerVersion;
    }
}
