using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Exercises reading a column type Npgsql has no client-side mapping for — an
/// unmapped composite — against a real Postgres server. Both halves of the fix
/// are covered: the describe-first text-format request that produces a real
/// Postgres literal without running the statement a second time, and the
/// per-cell placeholder that keeps one such column from failing an entire
/// result set where the request can't be made (a multi-statement command).
/// It also holds the 2026-09 security audit's live check: a <c>SELECT</c> of a
/// VOLATILE function that writes and returns the composite runs exactly once.
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

    // The placeholder is visible to users, so keep the expected text independent
    // of QueryEngine.UnreadableCell, which also constructs the actual value.
    private const string ExpectedUnreadableCell = "<unreadable public.pgnimbus_composite_scratch_addr>";
    private const string ScratchTable = "pgnimbus_composite_scratch";
    private const string CreateOrderFunction = "pgnimbus_composite_scratch_create_order";

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
             DROP FUNCTION IF EXISTS {CreateOrderFunction}();
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
            $"DROP FUNCTION IF EXISTS {CreateOrderFunction}(); DROP TABLE IF EXISTS {ScratchTable}; DROP TYPE IF EXISTS {CompositeType};",
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

            // The engine describes the statement first and requests the
            // composite column in text format on its single execution — no
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

            // Two statements in one command: UnknownResultTypeList would apply to
            // both and has to match each one's column count, so the text request
            // is skipped and the cell falls back per value. Before the per-cell
            // guard this surfaced as "Reading as 'System.Object' is not
            // supported…" with no rows at all.
            var rows = await DrainAsync(await engine.ExecuteAsync(
                $"SELECT id, ship_to FROM {ScratchTable}; SELECT 1",
                CancellationToken.None));

            await Assert.That(rows).Count().IsEqualTo(1);
            await Assert.That(rows[0][0]).IsEqualTo(1);
            await Assert.That(rows[0][1]).IsEqualTo(ExpectedUnreadableCell);
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

            // Every script statement is described before its one execution, so
            // the SELECT and the data-modifying RETURNING of the same column both
            // come back as literals — and the UPDATE runs once.
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
            await Assert.That(written.Rows[0][0]).IsEqualTo("(\"246 Oak St\",Milan)");

            // And the UPDATE ran exactly once — the whole point of describing first.
            var ids = await DrainAsync(
                await engine.ExecuteAsync($"SELECT id FROM {ScratchTable}", CancellationToken.None));
            await Assert.That(ids[0][0]).IsEqualTo(2);
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task AScriptStatementThatCannotReturnRowsRunsUndescribedAndOnce()
    {
        SkipIfNoConnection();

        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            // The INSERT is the script's second statement and returns nothing,
            // so it goes out without a describe (one round trip, not two); the
            // SELECT after it is still described and reads its literal.
            var results = new List<StatementResult>();
            await foreach (var result in engine.ExecuteScriptAsync(
                [
                    "SELECT 1",
                    $"INSERT INTO {ScratchTable} VALUES (2, ROW('1 Elm St', 'Rome')::{CompositeType})",
                    $"SELECT ship_to FROM {ScratchTable} WHERE id = 2",
                ],
                null))
            {
                results.Add(result);
            }

            await Assert.That(results).Count().IsEqualTo(3);
            await Assert.That(((CommandResult)results[1]).RowsAffected).IsEqualTo(1);
            await Assert.That(((MaterializedResultSet)results[2]).Rows[0][0]).IsEqualTo("(\"1 Elm St\",Rome)");

            var count = await DrainAsync(
                await engine.ExecuteAsync($"SELECT count(*) FROM {ScratchTable} WHERE id = 2", CancellationToken.None));
            await Assert.That(count[0][0]).IsEqualTo(1L);
        }
        finally
        {
            await DropAsync();
        }
    }

    [Test]
    public async Task AVolatileFunctionReturningACompositeRunsExactlyOnce()
    {
        SkipIfNoConnection();

        await SeedAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            // The security audit's live reproduction (2026-09, finding 1): a
            // SELECT whose only "read" is a call that inserts a row and returns
            // the table's composite. The old text fallback re-executed it —
            // one Run, two rows. Nothing about the statement says it writes, so
            // the only correct engine behaviour is to never execute twice.
            await using (var setup = await dataSource.OpenConnectionAsync())
            await using (var create = new NpgsqlCommand(
                $"""
                 CREATE OR REPLACE FUNCTION {CreateOrderFunction}() RETURNS {CompositeType}
                 LANGUAGE sql VOLATILE AS $$
                     INSERT INTO {ScratchTable} VALUES (2, ROW('1 Main St', 'Turin')::{CompositeType})
                     RETURNING ship_to;
                 $$;
                 """,
                setup))
            {
                await create.ExecuteNonQueryAsync();
            }

            var engine = new QueryEngine(dataSource);
            var rows = await DrainAsync(await engine.ExecuteAsync($"SELECT {CreateOrderFunction}()", CancellationToken.None));

            await Assert.That(rows).Count().IsEqualTo(1);
            await Assert.That(rows[0][0]).IsEqualTo("(\"1 Main St\",Turin)");

            var inserted = await DrainAsync(await engine.ExecuteAsync(
                $"SELECT count(*)::int FROM {ScratchTable} WHERE id = 2", CancellationToken.None));
            await Assert.That(inserted[0][0]).IsEqualTo(1);
        }
        finally
        {
            await DropAsync();
        }
    }
}
