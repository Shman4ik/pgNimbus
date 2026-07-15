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
}
