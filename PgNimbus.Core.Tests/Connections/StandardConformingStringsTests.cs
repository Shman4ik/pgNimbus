using Npgsql;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// The literals the app composes (<see cref="SqlLiteral"/>, the browse
/// filters) double only the quote, which is complete only under
/// <c>standard_conforming_strings = on</c>. A database owner can switch the
/// database's default off; the profile's startup option has to win over that
/// for every session, or a stored value like <c>x\'' OR 1=1 --</c> runs as
/// SQL the moment someone filters by it (security audit 2026-09, finding 13).
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like the other server tests. It changes
/// the <em>test database's</em> default for the few hundred milliseconds it
/// runs, which is why it is <c>NotInParallel</c>, and puts it back in
/// <c>finally</c>.
/// </summary>
[NotInParallel]
public class StandardConformingStringsTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private const string Table = "pgnimbus_scs_scratch";

    /// <summary>The value the audit used: under <c>scs = off</c> its <c>\'</c> ends the literal.</summary>
    private const string Hostile = @"x\'' OR 1=1 --";

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to check standard_conforming_strings against.");
        }
    }

    /// <summary>The test connection as a profile would build it, password included.</summary>
    private static (ConnectionProfile Profile, string? Password) ProfileFor(string connectionString)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "scs", csb.Host ?? "localhost", csb.Port, csb.Database ?? "postgres", csb.Username ?? "postgres",
            Core.Connections.SslMode.Prefer);
        return (profile, csb.Password);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ShowAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("SHOW standard_conforming_strings");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<int>> MatchingIdsAsync(NpgsqlDataSource dataSource, string predicate)
    {
        await using var command = dataSource.CreateCommand($"SELECT id FROM {Table} WHERE {predicate} ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<int>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    [Test]
    public async Task The_profile_forces_the_setting_on_over_a_database_that_turned_it_off()
    {
        SkipIfNoConnection();
        await using var admin = NpgsqlDataSource.Create(ConnectionString!);

        string database;
        await using (var current = admin.CreateCommand("SELECT current_database()"))
        {
            database = (string)(await current.ExecuteScalarAsync())!;
        }

        await ExecuteAsync(admin, $"DROP TABLE IF EXISTS {Table}; CREATE TABLE {Table} (id int PRIMARY KEY, note text)");
        await using (var insert = admin.CreateCommand($"INSERT INTO {Table} VALUES (1, @hostile), (2, 'other')"))
        {
            insert.Parameters.AddWithValue("hostile", Hostile);
            await insert.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(admin, $"ALTER DATABASE {SqlIdentifier.Quote(database)} SET standard_conforming_strings = off");
        try
        {
            // A fresh pool with no option of its own gets the database's
            // default — this is the hazard, and the proof the ALTER took.
            await using var plain = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                ApplicationName = "pgnimbus-scs-plain",
            }.ConnectionString);
            await Assert.That(await ShowAsync(plain)).IsEqualTo("off");

            // The same server through a profile: the startup option wins.
            var (profile, password) = ProfileFor(ConnectionString!);
            await using var throughProfile = NpgsqlDataSource.Create(profile.BuildConnectionString(password));
            await Assert.That(await ShowAsync(throughProfile)).IsEqualTo("on");

            // A browse filter on the hostile value, as the filter bar composes it.
            var predicate = RowFilterSql.ToPredicate(
                new RowFilter("note", FilterOperator.Equals, Hostile), ColumnValueEditor.Text, "text");
            await Assert.That(predicate).IsEqualTo(@"""note"" = E'x\\'''' OR 1=1 --'");

            // Through the profile the literal is the value and only its row matches.
            await Assert.That(await MatchingIdsAsync(throughProfile, predicate)).IsEquivalentTo(new[] { 1 });

            // Without the option too (a pooler that drops startup options): the
            // value holds a backslash, so SqlLiteral wrote an escape string, which
            // reads the same under either setting. A plain '…' literal here used to
            // end early and run " OR 1=1 --", returning every row.
            await Assert.That(await MatchingIdsAsync(plain, predicate)).IsEquivalentTo(new[] { 1 });

            var plainForm = $"\"note\" = '{Hostile.Replace("'", "''")}'";
            await Assert.That(await MatchingIdsAsync(plain, plainForm)).IsEquivalentTo(new[] { 1, 2 });
        }
        finally
        {
            await ExecuteAsync(admin, $"ALTER DATABASE {SqlIdentifier.Quote(database)} RESET standard_conforming_strings");
            await ExecuteAsync(admin, $"DROP TABLE IF EXISTS {Table}");
        }
    }

    [Test]
    public async Task A_read_only_profile_carries_both_options_and_the_server_honours_both()
    {
        SkipIfNoConnection();
        var (profile, password) = ProfileFor(ConnectionString!);
        await using var readOnly = NpgsqlDataSource.Create((profile with { ReadOnly = true }).BuildConnectionString(password));

        await Assert.That(await ShowAsync(readOnly)).IsEqualTo("on");
        await Assert.That(await new SchemaService(readOnly).GetWriteStateAsync(CancellationToken.None))
            .IsEqualTo(SessionWriteState.ReadOnly);
    }
}
