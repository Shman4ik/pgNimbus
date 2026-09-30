using System.Collections;
using System.Diagnostics;
using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// The byte half of the result caps (security audit 2026-09, finding 16): the
/// estimate itself, the budget, and — gated on <c>PGNIMBUS_TEST_CONN</c> — the
/// engine honouring it: a materialized result stops at its byte cap, a script's
/// sections share one budget without cancelling a later statement, and a stream
/// its consumer abandons is cancelled instead of drained.
/// </summary>
[NotInParallel]
public class ResultBudgetTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read results from.");
        }
    }

    [Test]
    public async Task The_estimate_counts_text_bytea_and_arrays_by_length()
    {
        var small = ResultBudget.EstimateRow([1, "a", null]);
        var text = ResultBudget.EstimateRow([1, new string('x', 1_000_000), null]);
        var bytes = ResultBudget.EstimateRow([1, new byte[1_000_000], null]);
        var array = ResultBudget.EstimateRow([1, Enumerable.Repeat("abcdefghij", 100_000).ToArray(), null]);
        var matrix = ResultBudget.EstimateRow([new int[1000, 1000]]);
        var bits = ResultBudget.EstimateRow([new BitArray(8_000_000)]);

        await Assert.That(small).IsLessThan(200);
        // UTF-16: two bytes a character.
        await Assert.That(text).IsGreaterThanOrEqualTo(2_000_000);
        await Assert.That(bytes).IsGreaterThanOrEqualTo(1_000_000);
        await Assert.That(array).IsGreaterThanOrEqualTo(100_000 * 20);
        await Assert.That(matrix).IsGreaterThanOrEqualTo(1_000_000 * 8);
        await Assert.That(bits).IsGreaterThanOrEqualTo(1_000_000);
    }

    [Test]
    public async Task Null_cells_still_cost_their_slot()
    {
        // A server claiming 65,535 columns of NULL is half a megabyte a row, not nothing.
        await Assert.That(ResultBudget.EstimateRow(new object?[65_535])).IsGreaterThanOrEqualTo(65_535 * 8);
    }

    [Test]
    public async Task The_budget_refuses_the_row_that_would_not_fit_and_says_why()
    {
        var byRows = new ResultBudget(maxRows: 2, maxBytes: long.MaxValue);
        await Assert.That(byRows.TryTake(1)).IsTrue();
        await Assert.That(byRows.TryTake(1)).IsTrue();
        await Assert.That(byRows.TryTake(1)).IsFalse();
        await Assert.That(byRows.Exhausted).IsEqualTo(ResultCap.Rows);

        var byBytes = new ResultBudget(maxRows: 100, maxBytes: 1_000);
        await Assert.That(byBytes.TryTake(600)).IsTrue();
        await Assert.That(byBytes.TryTake(600)).IsFalse();
        await Assert.That(byBytes.Exhausted).IsEqualTo(ResultCap.Bytes);
        // A refused row charges nothing; a smaller one still fits.
        await Assert.That(byBytes.TryTake(300)).IsTrue();
        await Assert.That(byBytes.Rows).IsEqualTo(2);
        await Assert.That(byBytes.Bytes).IsEqualTo(900);
    }

    [Test]
    public async Task A_materialized_result_stops_at_its_byte_cap()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var engine = new QueryEngine(dataSource);

        await engine.BeginTransactionAsync(CancellationToken.None);
        try
        {
            // Twenty 1 MB rows (2 MB each as .NET strings) against a 5 MB cap.
            var result = await engine.ExecuteAsync(
                "SELECT repeat('x', 1000000) FROM generate_series(1, 20)", CancellationToken.None, maxRows: 1000, maxBytes: 5L * 1024 * 1024);

            var set = (MaterializedResultSet)result;
            await Assert.That(set.Truncated).IsTrue();
            await Assert.That(set.CappedBy).IsEqualTo(ResultCap.Bytes);
            await Assert.That(set.Rows.Count).IsEqualTo(2);
        }
        finally
        {
            await engine.RollbackAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_scripts_sections_share_one_budget_and_a_later_statement_still_runs()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await ExecAsync(dataSource, "DROP TABLE IF EXISTS pgnimbus_budget_scratch; CREATE TABLE pgnimbus_budget_scratch (a int)");
        try
        {
            var engine = new QueryEngine(dataSource);
            var budget = new ResultBudget(maxRows: 150, maxBytes: long.MaxValue);

            var results = new List<StatementResult>();
            await foreach (var result in engine.ExecuteScriptAsync(
                [
                    "SELECT g FROM generate_series(1, 100) g",
                    "SELECT g FROM generate_series(1, 100) g",
                    "INSERT INTO pgnimbus_budget_scratch SELECT g FROM generate_series(1, 10) g RETURNING a",
                ],
                maxRowsPerStatement: 1000,
                CancellationToken.None,
                budget))
            {
                results.Add(result);
            }

            await Assert.That(results.Count).IsEqualTo(3);
            var first = (MaterializedResultSet)results[0];
            var second = (MaterializedResultSet)results[1];
            var insert = (MaterializedResultSet)results[2];
            await Assert.That(first.Rows.Count).IsEqualTo(100);
            await Assert.That(first.Truncated).IsFalse();
            await Assert.That(second.Rows.Count).IsEqualTo(50);
            await Assert.That(second.CappedBy).IsEqualTo(ResultCap.Shared);

            // The budget was spent before the INSERT … RETURNING: it keeps no rows, but
            // it is read to the end rather than cancelled, so every row went in.
            await Assert.That(insert.Rows).IsEmpty();
            await Assert.That(insert.CappedBy).IsEqualTo(ResultCap.Shared);
            await Assert.That(await ScalarAsync(dataSource, "SELECT count(*) FROM pgnimbus_budget_scratch")).IsEqualTo(10L);
        }
        finally
        {
            await ExecAsync(dataSource, "DROP TABLE IF EXISTS pgnimbus_budget_scratch");
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_stream_its_consumer_abandons_is_cancelled_not_drained(CancellationToken ct)
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var engine = new QueryEngine(dataSource);

        // Fifty million rows: draining them on disposal would take far longer than the bound below.
        var result = (ResultSet)await engine.ExecuteAsync("SELECT g, repeat('y', 100) FROM generate_series(1, 50000000) g", ct);
        var stopwatch = Stopwatch.StartNew();
        var seen = 0;
        await foreach (var batch in result.Batches)
        {
            seen += batch.Rows.Count;
            if (seen >= 1000)
            {
                break;
            }
        }

        await Assert.That(seen).IsGreaterThanOrEqualTo(1000);
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));

        // The pool is usable afterwards.
        await Assert.That(await ScalarAsync(dataSource, "SELECT 42::bigint")).IsEqualTo(42L);
    }

    private static async Task ExecAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
