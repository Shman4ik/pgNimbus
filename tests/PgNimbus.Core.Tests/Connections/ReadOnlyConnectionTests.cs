using Npgsql;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// A read-only profile is only worth having if the server enforces it for every
/// session the pool hands out, including one that turned it off and was then
/// returned. The offline half checks what the profile asks for and that older
/// connections.json files still load; the rest (gated on
/// <c>PGNIMBUS_TEST_CONN</c>) checks what a real server does with it.
/// </summary>
[NotInParallel]
public class ReadOnlyConnectionTests
{
    private static ConnectionProfile Profile(bool readOnly) =>
        new(Guid.NewGuid(), "p", "db.example.com", 5432, "app", "me", Core.Connections.SslMode.Prefer, ReadOnly: readOnly);

    [Test]
    public async Task A_read_only_profile_starts_every_session_read_only()
    {
        var builder = new NpgsqlConnectionStringBuilder(Profile(readOnly: true).BuildConnectionString("pw"));
        await Assert.That(builder.Options).IsEqualTo(
            "-c default_transaction_read_only=on -c standard_conforming_strings=on");

        // The read-only flag is read back out of Options by the window builder,
        // so it has to stay findable beside the option every profile carries.
        await Assert.That(builder.Options!.Contains(ConnectionProfile.ReadOnlySessionOption, StringComparison.Ordinal)).IsTrue();

        var plain = new NpgsqlConnectionStringBuilder(Profile(readOnly: false).BuildConnectionString("pw"));
        await Assert.That(plain.Options).IsEqualTo("-c standard_conforming_strings=on");
        await Assert.That(plain.Options!.Contains(ConnectionProfile.ReadOnlySessionOption, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task A_connection_string_from_outside_a_profile_gets_the_standard_strings_option_once()
    {
        // PGNIMBUS_CONN never passes through a profile; the option is added there.
        var bare = ConnectionProfile.WithStandardStrings("Host=h;Database=d;Username=u");
        await Assert.That(new NpgsqlConnectionStringBuilder(bare).Options).IsEqualTo(ConnectionProfile.StandardStringsSessionOption);

        var withOthers = ConnectionProfile.WithStandardStrings("Host=h;Database=d;Username=u;Options=-c search_path=app");
        await Assert.That(new NpgsqlConnectionStringBuilder(withOthers).Options)
            .IsEqualTo("-c search_path=app -c standard_conforming_strings=on");

        // Already last: left alone.
        var already = "Host=h;Database=d;Username=u;Options=-c standard_conforming_strings=on";
        await Assert.That(ConnectionProfile.WithStandardStrings(already)).IsEqualTo(already);

        // Turned off by the string itself: the option is appended after it, and
        // the server applies -c switches in order, so the last one wins.
        var off = ConnectionProfile.WithStandardStrings("Host=h;Database=d;Username=u;Options=-c standard_conforming_strings=off");
        await Assert.That(new NpgsqlConnectionStringBuilder(off).Options)
            .IsEqualTo("-c standard_conforming_strings=off -c standard_conforming_strings=on");
    }

    [Test]
    public async Task The_flag_round_trips_and_a_file_from_before_it_loads_as_read_write()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-readonly-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "connections.json");
            var store = new ConnectionProfileStore(path);
            store.Save([Profile(readOnly: true)]);
            await Assert.That(store.Load().Single().ReadOnly).IsTrue();

            // Written by a version that had no such field.
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
                    "AccentColor": null,
                    "SshTunnel": null
                  }
                ]
                """);
            var legacy = store.Load().Single();
            await Assert.That(legacy.Name).IsEqualTo("old");
            await Assert.That(legacy.ReadOnly).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // --- Against a real server ----------------------------------------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private const string Table = "pgnimbus_readonly_scratch";

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to connect read-only to.");
        }
    }

    // The test connection with the read-only option a profile would add. One
    // pooled connection, so a reopen is the same server session every time.
    private static NpgsqlDataSource ReadOnlySource() =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Options = ConnectionProfile.ReadOnlySessionOption,
            MaxPoolSize = 1,
        }.ConnectionString);

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task The_server_refuses_writes_and_still_answers_reads()
    {
        SkipIfNoConnection();
        await using var writable = NpgsqlDataSource.Create(ConnectionString!);
        await ExecuteAsync(writable, $"DROP TABLE IF EXISTS {Table}; CREATE TABLE {Table} (id int)");
        try
        {
            await using var readOnly = ReadOnlySource();
            await using (var select = readOnly.CreateCommand($"SELECT count(*) FROM {Table}"))
            {
                await Assert.That(await select.ExecuteScalarAsync()).IsEqualTo(0L);
            }

            foreach (var write in new[] { $"INSERT INTO {Table} VALUES (1)", $"CREATE TABLE {Table}_2 (id int)", $"TRUNCATE {Table}" })
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(readOnly, write));
                await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.ReadOnlySqlTransaction);
            }
        }
        finally
        {
            await ExecuteAsync(writable, $"DROP TABLE IF EXISTS {Table}");
        }
    }

    [Test]
    public async Task A_session_that_turned_it_off_goes_back_to_the_pool_read_only()
    {
        // The pool resets a connection it gets back, and a startup option is the
        // session's default, so RESET brings it back rather than clearing it.
        SkipIfNoConnection();
        await using var readOnly = ReadOnlySource();

        await using (var connection = await readOnly.OpenConnectionAsync())
        await using (var off = new NpgsqlCommand("SET default_transaction_read_only = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var again = await readOnly.OpenConnectionAsync();
        await using var show = new NpgsqlCommand("SHOW default_transaction_read_only", again);
        await Assert.That(await show.ExecuteScalarAsync()).IsEqualTo("on");
    }

    [Test]
    public async Task The_server_reports_the_write_state_of_a_new_session()
    {
        SkipIfNoConnection();
        await using var readOnly = ReadOnlySource();
        await using var writable = NpgsqlDataSource.Create(ConnectionString!);

        await Assert.That(await new SchemaService(readOnly).GetWriteStateAsync(CancellationToken.None))
            .IsEqualTo(SessionWriteState.ReadOnly);
        await Assert.That(await new SchemaService(writable).GetWriteStateAsync(CancellationToken.None))
            .IsEqualTo(SessionWriteState.ReadWrite);
    }
}
