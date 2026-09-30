using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Tests;

/// <summary>
/// Completion's catalog read from a real server rather than built in memory:
/// the part of the name-resolution contract (T05, T35) that only a live
/// session can show. Two schemas carry a same-named table, and the question is
/// always the same — does completion resolve <c>twin</c> to the table the
/// server would run the statement against?
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like the Core integration tests: unset,
/// every test here skips.
/// </summary>
[NotInParallel]
public class CompletionLiveCatalogTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read a catalog from.");
        }
    }

    private static NpgsqlDataSource DataSource(string searchPath) =>
        new NpgsqlDataSourceBuilder(ConnectionString!) { ConnectionStringBuilder = { SearchPath = searchPath } }.Build();

    private static async Task SeedAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var command = dataSource.CreateCommand(
            """
            DROP SCHEMA IF EXISTS pgn_cmp_a CASCADE;
            DROP SCHEMA IF EXISTS pgn_cmp_b CASCADE;
            CREATE SCHEMA pgn_cmp_a;
            CREATE SCHEMA pgn_cmp_b;
            CREATE TABLE pgn_cmp_a.twin (id int, only_in_a text);
            CREATE TABLE pgn_cmp_b.twin (id int, only_in_b text);
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var command = dataSource.CreateCommand(
            "DROP SCHEMA IF EXISTS pgn_cmp_a CASCADE; DROP SCHEMA IF EXISTS pgn_cmp_b CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static string[] Columns(SqlCompletionProvider provider, string marked)
    {
        var caret = marked.IndexOf('|');
        return [.. provider.GetCompletionData(marked.Remove(caret, 1), caret)
            .Where(i => i.Kind == SqlCompletionKind.Column)
            .Select(i => i.InsertText)];
    }

    [Test]
    public async Task A_short_name_resolves_along_the_connections_search_path()
    {
        SkipIfNoConnection();
        await SeedAsync();
        try
        {
            foreach (var (path, expected, absent) in new[]
                     {
                         ("pgn_cmp_b, public", "only_in_b", "only_in_a"),
                         ("pgn_cmp_a, pgn_cmp_b", "only_in_a", "only_in_b"),
                     })
            {
                await using var dataSource = DataSource(path);
                var provider = new SqlCompletionProvider(new SchemaService(dataSource));
                await provider.RefreshAsync(CancellationToken.None);

                var columns = Columns(provider, "SELECT * FROM twin t WHERE t.|");
                await Assert.That(columns).Contains(expected);
                await Assert.That(columns).DoesNotContain(absent);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // A SET search_path run in a query tab outside a transaction goes down a
    // pooled connection, and Npgsql resets session state when that connection
    // goes back to the pool — so the tab's *next* Run resolves along the
    // connection default again. Completion reads that same default, so the two
    // agree. This pins the premise: if the pool ever stopped resetting, a SET
    // would start to steer Runs while completion kept the default.
    [Test]
    public async Task A_set_outside_a_transaction_does_not_outlive_its_statement()
    {
        SkipIfNoConnection();
        await SeedAsync();
        try
        {
            await using var dataSource = DataSource("pgn_cmp_b, public");
            var engine = new QueryEngine(dataSource);
            await engine.ExecuteAsync("SET search_path TO pgn_cmp_a", CancellationToken.None);

            var provider = new SqlCompletionProvider(new SchemaService(dataSource));
            await provider.RefreshAsync(CancellationToken.None);
            await Assert.That(Columns(provider, "SELECT * FROM twin t WHERE t.|")).Contains("only_in_b");

            var result = await engine.ExecuteAsync("SELECT * FROM twin", CancellationToken.None);
            var resultSet = (ResultSet)result;
            await Assert.That(resultSet.Columns.Select(c => c.Name)).Contains("only_in_b");
            await foreach (var _ in resultSet.Batches)
            {
            }
        }
        finally
        {
            await DropAsync();
        }
    }
}
