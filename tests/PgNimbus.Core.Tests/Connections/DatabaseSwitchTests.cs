using Npgsql;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// The connect behind the title bar's database list (and Open in New Window):
/// the window's profile with only the database swapped, connected before
/// anything changes. Run as an ordinary role, since a superuser passes every
/// CONNECT check.
/// </summary>
[NotInParallel(nameof(DatabaseSwitchTests))]
public sealed class DatabaseSwitchTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Switching_reaches_a_database_the_role_may_connect_to_and_is_refused_by_one_it_may_not()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to switch databases on.");
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var role = $"pgn_switcher_{suffix}";
        var open = $"pgn_open_{suffix}";
        var locked = $"pgn_locked_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(ConnectionString!);
        var server = new NpgsqlConnectionStringBuilder(ConnectionString);
        var home = server.Database ?? "postgres";

        await ExecuteAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
        await ExecuteAsync(admin, $"CREATE DATABASE {open}");
        await ExecuteAsync(admin, $"CREATE DATABASE {locked}");
        await ExecuteAsync(admin, $"REVOKE CONNECT ON DATABASE {locked} FROM PUBLIC");
        try
        {
            var profile = new ConnectionProfile(
                Guid.NewGuid(), "switch test", server.Host!, server.Port, home, role, PgNimbus.Core.Connections.SslMode.Prefer);

            // The list the flyout shows, read as that role: the locked one is not offered.
            await using (var homeSource = profile.CreateDataSource(password))
            {
                var listed = await new SchemaService(homeSource).GetDatabasesAsync(CancellationToken.None);
                await Assert.That(listed).Contains(open);
                await Assert.That(listed).Contains(home);
                await Assert.That(listed).DoesNotContain(locked);
            }

            var (target, dataSource) = await DatabaseSwitch.ConnectAsync(profile, open, password, tunnelEndpoint: null);
            await using (dataSource)
            {
                await Assert.That(target.Database).IsEqualTo(open);
                await Assert.That(target.Id).IsEqualTo(profile.Id);
                await Assert.That(profile.Database).IsEqualTo(home);
                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = new NpgsqlCommand("SELECT current_database(), current_user", connection);
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                await Assert.That(reader.GetString(0)).IsEqualTo(open);
                await Assert.That(reader.GetString(1)).IsEqualTo(role);
            }

            // Refused before anything is handed back; the caller's profile is as it was.
            var refused = await Assert.ThrowsAsync<PostgresException>(
                () => DatabaseSwitch.ConnectAsync(profile, locked, password, tunnelEndpoint: null));
            await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
            await Assert.That(profile.Database).IsEqualTo(home);

            // So is a database that is gone.
            await Assert.ThrowsAsync<PostgresException>(
                () => DatabaseSwitch.ConnectAsync(profile, $"pgn_gone_{suffix}", password, tunnelEndpoint: null));

            // The PGNIMBUS_CONN form: a connection string with the database swapped.
            var plain = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = role, Password = null, Database = home }.ConnectionString;
            await using (var fromString = await DatabaseSwitch.ConnectAsync(plain, open, password))
            {
                await using var connection = await fromString.OpenConnectionAsync();
                await Assert.That(connection.Database).IsEqualTo(open);
            }

            await Assert.ThrowsAsync<PostgresException>(() => DatabaseSwitch.ConnectAsync(plain, locked, password));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {open} WITH (FORCE)");
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {locked} WITH (FORCE)");
            await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {role}");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
