using Npgsql;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// A production profile (ROADMAP T3) opens with writes off, and one window can
/// allow them for itself. The offline half checks what each profile asks for,
/// that the environment is saved and that files from before it still load; the
/// rest (gated on <c>PGNIMBUS_TEST_CONN</c>) checks that the server refuses a
/// write until the gate allows it, and again after it locks.
/// </summary>
[NotInParallel]
public class ProductionWriteGateTests
{
    private static ConnectionProfile Profile(ConnectionEnvironment environment, bool readOnly = false) =>
        new(Guid.NewGuid(), "p", "db.example.com", 5432, "app", "me", Core.Connections.SslMode.Prefer,
            ReadOnly: readOnly, Environment: environment);

    private static string? OptionsOf(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Options;

    [Test]
    public async Task The_enum_keeps_its_numbers()
    {
        // Persisted as numbers: a renumbering would relabel every saved profile.
        var numbered = string.Join(",", Enum.GetValues<ConnectionEnvironment>().Select(e => $"{e}={(int)e}"));
        await Assert.That(numbered).IsEqualTo("None=0,Development=1,Staging=2,Production=3");
    }

    [Test]
    public async Task Only_production_starts_read_only_and_only_a_writable_one_can_allow_writes()
    {
        foreach (var environment in new[] { ConnectionEnvironment.None, ConnectionEnvironment.Development, ConnectionEnvironment.Staging })
        {
            var profile = Profile(environment);
            await Assert.That(profile.StartsReadOnly).IsFalse();
            await Assert.That(profile.CanAllowWrites).IsFalse();
            await Assert.That(OptionsOf(profile.BuildConnectionString("pw"))).IsEqualTo(ConnectionProfile.StandardStringsSessionOption);
        }

        var production = Profile(ConnectionEnvironment.Production);
        await Assert.That(production.StartsReadOnly).IsTrue();
        await Assert.That(production.CanAllowWrites).IsTrue();
        await Assert.That(OptionsOf(production.BuildConnectionString("pw"))).IsEqualTo(ConnectionProfile.SessionOptions(readOnly: true));

        // The writable pool the gate builds: the same string without the option.
        await Assert.That(OptionsOf(production.BuildConnectionString("pw", readOnly: false))).IsEqualTo(ConnectionProfile.StandardStringsSessionOption);

        // Read-only is a promise: a read-only production profile can't lift it.
        var readOnlyProduction = Profile(ConnectionEnvironment.Production, readOnly: true);
        await Assert.That(readOnlyProduction.StartsReadOnly).IsTrue();
        await Assert.That(readOnlyProduction.CanAllowWrites).IsFalse();
    }

    [Test]
    public async Task The_environment_round_trips_and_a_file_from_before_it_loads_as_none()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-environment-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "connections.json");
            var store = new ConnectionProfileStore(path);
            store.Save([Profile(ConnectionEnvironment.Production)]);
            await Assert.That(store.Load().Single().Environment).IsEqualTo(ConnectionEnvironment.Production);

            // The derived flags are not written next to it.
            var json = File.ReadAllText(path);
            await Assert.That(json).DoesNotContain("StartsReadOnly");
            await Assert.That(json).DoesNotContain("CanAllowWrites");

            File.WriteAllText(path, """
                [
                  {
                    "Id": "8f6b3c1e-2f4a-4c55-9d7e-0a1b2c3d4e5f",
                    "Name": "old",
                    "Host": "db.example.com",
                    "Port": 5432,
                    "Database": "app",
                    "Username": "me",
                    "SslMode": 2,
                    "ReadOnly": false
                  }
                ]
                """);
            var legacy = store.Load().Single();
            await Assert.That(legacy.Environment).IsEqualTo(ConnectionEnvironment.None);
            await Assert.That(legacy.StartsReadOnly).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task A_gate_without_a_writable_pool_cannot_allow_writes()
    {
        await using var primary = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=d;Username=u");
        WriteGatedDataSource gate = primary;

        await Assert.That(gate.CanAllowWrites).IsFalse();
        await Assert.That(gate.WritesAllowed).IsFalse();
        await Assert.That(gate.ConnectionString).IsEqualTo(primary.ConnectionString);
        await Assert.That(() => gate.AllowWrites()).Throws<InvalidOperationException>();

        // Locking what was never allowed is harmless.
        gate.LockWrites();
        await Assert.That(gate.WritesAllowed).IsFalse();
    }

    [Test]
    public async Task The_writable_pool_is_built_once_and_only_when_writes_are_allowed()
    {
        var built = 0;
        var gate = new WriteGatedDataSource(
            NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=d;Username=u"),
            () =>
            {
                built++;
                return NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=d;Username=u;Application Name=writable");
            });
        await using (gate)
        {
            await Assert.That(gate.CanAllowWrites).IsTrue();
            await Assert.That(built).IsEqualTo(0);

            gate.AllowWrites();
            await Assert.That(gate.WritesAllowed).IsTrue();
            await Assert.That(built).IsEqualTo(1);
            gate.LockWrites();
            await Assert.That(gate.WritesAllowed).IsFalse();
            gate.AllowWrites();
            await Assert.That(built).IsEqualTo(1);
        }
    }

    // --- Against a real server ----------------------------------------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private const string Table = "pgnimbus_production_gate_scratch";

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to gate writes on.");
        }
    }

    // As App.BuildMainWindow builds a production window's gate: the read-only
    // pool, and a writable one built on demand. One connection each, so a
    // reopen is the same server session.
    private static WriteGatedDataSource Gate() =>
        new(
            NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Options = ConnectionProfile.SessionOptions(readOnly: true),
                MaxPoolSize = 1,
            }.ConnectionString),
            () => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Options = ConnectionProfile.SessionOptions(readOnly: false),
                MaxPoolSize = 1,
            }.ConnectionString));

    private static async Task ExecuteAsync(WriteGatedDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(WriteGatedDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task RefusedAsync(Func<Task> write)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(write);
        await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.ReadOnlySqlTransaction);
    }

    [Test]
    public async Task Writes_are_refused_until_allowed_and_again_once_locked()
    {
        SkipIfNoConnection();
        await using WriteGatedDataSource setup = NpgsqlDataSource.Create(ConnectionString!);
        await ExecuteAsync(setup, $"DROP TABLE IF EXISTS {Table}; CREATE TABLE {Table} (id int)");
        try
        {
            await using var gate = Gate();
            var schema = new SchemaService(gate);

            // As the window opens: the server says read-only and refuses writes,
            // through a service as well as through a command.
            await Assert.That(await schema.GetWriteStateAsync(CancellationToken.None)).IsEqualTo(SessionWriteState.ReadOnly);
            await RefusedAsync(() => ExecuteAsync(gate, $"INSERT INTO {Table} VALUES (1)"));
            await RefusedAsync(() => new QueryEngine(gate).ExecuteNonQueryAsync(
                $"INSERT INTO {Table} VALUES (2)", new Dictionary<string, object?>(), CancellationToken.None));

            gate.AllowWrites();
            await Assert.That(await schema.GetWriteStateAsync(CancellationToken.None)).IsEqualTo(SessionWriteState.ReadWrite);
            await ExecuteAsync(gate, $"INSERT INTO {Table} VALUES (3)");

            gate.LockWrites();
            await Assert.That(await schema.GetWriteStateAsync(CancellationToken.None)).IsEqualTo(SessionWriteState.ReadOnly);
            await RefusedAsync(() => ExecuteAsync(gate, $"INSERT INTO {Table} VALUES (4)"));

            await Assert.That(await ScalarAsync(setup, $"SELECT string_agg(id::text, ',' ORDER BY id) FROM {Table}")).IsEqualTo("3");
        }
        finally
        {
            await ExecuteAsync(setup, $"DROP TABLE IF EXISTS {Table}");
        }
    }

    [Test]
    public async Task A_locked_window_never_reuses_a_writable_session()
    {
        // The writable pool's idle connection is closed on lock, and the
        // read-only pool's session that turned the setting off is reset to the
        // startup option when it comes back, so nothing writable is left over.
        SkipIfNoConnection();
        await using var gate = Gate();

        gate.AllowWrites();
        await Assert.That(await ScalarAsync(gate, "SHOW default_transaction_read_only")).IsEqualTo("off");
        gate.LockWrites();
        await Assert.That(await ScalarAsync(gate, "SHOW default_transaction_read_only")).IsEqualTo("on");

        await using (var connection = await gate.OpenConnectionAsync())
        await using (var off = new NpgsqlCommand("SET default_transaction_read_only = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await Assert.That(await ScalarAsync(gate, "SHOW default_transaction_read_only")).IsEqualTo("on");
    }
}
