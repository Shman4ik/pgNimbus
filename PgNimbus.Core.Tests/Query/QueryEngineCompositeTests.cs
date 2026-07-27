using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Exercises reading a column type Npgsql has no client-side mapping for — an
/// unmapped composite — against a real Postgres server. Both halves of the fix
/// are covered: the text-format re-execution that produces a real Postgres
/// literal for a statement it's provably harmless to run twice, and the
/// per-cell placeholder that keeps one such column from failing an entire
/// result set when it isn't.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> exactly like
/// <see cref="QueryEngineReconnectTests"/>: unset (a plain local `dotnet test`),
/// every test here skips cleanly; CI's <c>postgres:17</c> service container
/// sets it so these actually run.
/// </summary>
// Every test here drops and recreates the same scratch type and table, so they
// must not overlap with each other.
[NotInParallel]
public class QueryEngineCompositeTests
{
    private const string CompositeType = "pgnimbus_composite_scratch_addr";

    // Npgsql reports a composite by its schema-qualified name, which is what the
    // placeholder carries.
    private const string QualifiedCompositeType = "public." + CompositeType;
    private const string ScratchTable = "pgnimbus_composite_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to test composite reads against.");
        }
    }

    private static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString!);

    // A composite type with no Npgsql mapping, plus one row using it. Dropped and
    // recreated per test so a previous crashed run can't leave a stale shape behind.
    //
    // Seeding runs on its own throwaway data source, and the engine's is only
    // created afterwards: Npgsql snapshots pg_type when a data source first
    // connects, so a type created later reads back as the unknown-type name "-.-"
    // rather than its real one — an artifact of creating the type mid-test that
    // no real session ever sees.
    private static async Task SeedAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
             DROP TABLE IF EXISTS {ScratchTable};
             DROP TYPE IF EXISTS {CompositeType};
             CREATE TYPE {CompositeType} AS (street text, city text);
             CREATE TABLE {ScratchTable} (id int, ship_to {CompositeType});
             INSERT INTO {ScratchTable} VALUES (1, ROW('246 Oak St', 'Milan')::{CompositeType});
             """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP TABLE IF EXISTS {ScratchTable}; DROP TYPE IF EXISTS {CompositeType};",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<object?[]>> DrainAsync(StatementResult result)
    {
        if (result is not ResultSet resultSet)
        {
            var detail = result is QueryError error ? $": {error.Message}" : string.Empty;
            throw new InvalidOperationException($"Expected a ResultSet but got {result.GetType().Name}{detail}");
        }

        var rows = new List<object?[]>();
        await foreach (var batch in resultSet.Batches)
        {
            rows.AddRange(batch.Rows);
        }

        return rows;
    }

    [Test]
    public async Task ReadOnlyStatementReadsACompositeAsItsPostgresLiteral()
    {
        SkipIfNoConnection();

        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            // A plain SELECT is provably harmless to run twice, so the engine
            // re-requests the composite column in text format on its own — no
            // caller opt-in, which is what a hand-written query gets.
            var rows = await DrainAsync(
                await engine.ExecuteAsync($"SELECT ship_to FROM {ScratchTable}", CancellationToken.None));

            await Assert.That(rows).Count().IsEqualTo(1);
            await Assert.That(rows[0][0]).IsEqualTo("(\"246 Oak St\",Milan)");
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task StatementThatMustNotReRunGetsAPlaceholderInsteadOfFailing()
    {
        SkipIfNoConnection();

        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            // Two statements in one command: re-executing would run both again, so
            // the fallback is refused. Before the per-cell guard this surfaced as
            // "Reading as 'System.Object' is not supported…" with no rows at all.
            var rows = await DrainAsync(await engine.ExecuteAsync(
                $"SELECT id, ship_to FROM {ScratchTable}; SELECT 1",
                CancellationToken.None));

            await Assert.That(rows).Count().IsEqualTo(1);
            await Assert.That(rows[0][0]).IsEqualTo(1);
            await Assert.That(rows[0][1]).IsEqualTo(QueryEngine.UnreadableCell(QualifiedCompositeType));
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task ScriptStatementsAreVettedIndividually()
    {
        SkipIfNoConnection();

        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            // The script path never vouches for its statements (they're arbitrary
            // SQL), so each one stands on its own: the SELECT earns the literal,
            // while a data-modifying RETURNING of the same column must not be
            // re-run and falls back per cell.
            var results = new List<StatementResult>();
            await foreach (var result in engine.ExecuteScriptAsync(
                [
                    $"SELECT ship_to FROM {ScratchTable}",
                    $"UPDATE {ScratchTable} SET id = id + 1 RETURNING ship_to",
                ],
                null))
            {
                results.Add(result);
            }

            await Assert.That(results).Count().IsEqualTo(2);
            var read = (MaterializedResultSet)results[0];
            var written = (MaterializedResultSet)results[1];

            await Assert.That(read.Rows[0][0]).IsEqualTo("(\"246 Oak St\",Milan)");
            await Assert.That(written.Rows[0][0]).IsEqualTo(QueryEngine.UnreadableCell(QualifiedCompositeType));

            // And the UPDATE ran exactly once — the whole point of refusing it.
            var ids = await DrainAsync(
                await engine.ExecuteAsync($"SELECT id FROM {ScratchTable}", CancellationToken.None));
            await Assert.That(ids[0][0]).IsEqualTo(2);
        }
        finally
        {
            await DropAsync();
        }
    }
}
