using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// What <see cref="ExplainService.ExplainAsync"/> will and will not send. The
/// 2026-09 security audit (finding 3) explained a selection of two statements
/// and found the second one executed: EXPLAIN plans only the first statement
/// of its text while the server runs them all. The refusal is checked without
/// a server; the live half, gated on <c>PGNIMBUS_TEST_CONN</c> like the
/// engine's own integration tests, is the audit's reproduction with the table
/// checked afterwards.
/// </summary>
[NotInParallel]
public class ExplainServiceTests
{
    private const string ScratchTable = "pgnimbus_explain_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to explain against.");
        }
    }

    [Test]
    [Arguments("SELECT 1", "SELECT 1")]
    [Arguments("SELECT 1;", "SELECT 1")]
    [Arguments("  -- the plan\n  SELECT 1;  \n", "-- the plan\n  SELECT 1")]
    [Arguments("SELECT 'a;b'; ", "SELECT 'a;b'")]
    public async Task One_statement_is_handed_on_without_its_terminator(string sql, string expected)
    {
        await Assert.That(ExplainService.SingleStatement(sql)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SELECT 1; CREATE TABLE t (id int)", 2)]
    [Arguments("SELECT * FROM t;\nDELETE FROM t WHERE id = 1;", 2)]
    [Arguments("BEGIN; UPDATE t SET n = 1; COMMIT;", 3)]
    [Arguments("SELECT 1 --x\r; COMMIT; CREATE TABLE t (id int)", 3)]
    public async Task Several_statements_are_refused_with_their_count(string sql, int count)
    {
        var thrown = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            ExplainService.SingleStatement(sql);
            await Task.CompletedTask;
        });
        await Assert.That(thrown!.Message).Contains($"{count} were given");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("-- only a comment")]
    public async Task Nothing_to_explain_is_refused(string sql)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            ExplainService.SingleStatement(sql);
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task The_refusal_happens_before_any_connection_is_opened()
    {
        // TEST-NET-3 never answers, so reaching the server would hang until the
        // connect timeout; the refusal has to come first.
        await using var dataSource = NpgsqlDataSource.Create("Host=203.0.113.1;Username=x;Timeout=1");
        var service = new ExplainService(dataSource);

        var thrown = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ExplainAsync("SELECT 1; SELECT 2", analyze: false, CancellationToken.None));
        await Assert.That(thrown!.Message).Contains("2 were given");
    }

    [Test]
    public async Task Explaining_two_statements_runs_neither()
    {
        SkipIfNoConnection();

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var setup = await dataSource.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {ScratchTable}", setup))
        {
            await drop.ExecuteNonQueryAsync();
        }

        try
        {
            var service = new ExplainService(dataSource);
            // The audit's reproduction, and two shapes the always-rolled-back
            // transaction alone would not stop: a COMMIT ends that transaction,
            // and a bare \r ends a -- comment for the server (and, since the
            // review of these fixes, for SqlLexer too).
            foreach (var sql in new[]
            {
                $"SELECT 1; CREATE TABLE {ScratchTable} (id int)",
                $"SELECT 1; COMMIT; CREATE TABLE {ScratchTable} (id int)",
                $"SELECT 1 --x\r; COMMIT; CREATE TABLE {ScratchTable} (id int)",
            })
            {
                await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await service.ExplainAsync(sql, analyze: false, CancellationToken.None));
                await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await service.ExplainAsync(sql, analyze: true, CancellationToken.None));
            }

            await Assert.That(await TableExistsAsync(dataSource)).IsFalse();

            // One statement still gets its plan, with the terminator tolerated.
            var run = await service.ExplainAsync("SELECT 1;", analyze: false, CancellationToken.None);
            await Assert.That(run.Result.Root.NodeType).IsEqualTo("Result");
        }
        finally
        {
            await using var cleanup = await dataSource.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {ScratchTable}", cleanup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Test]
    public async Task A_plain_explain_of_a_statement_the_planner_has_to_run_persists_nothing()
    {
        SkipIfNoConnection();

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var setup = await dataSource.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {ScratchTable}", setup))
        {
            await drop.ExecuteNonQueryAsync();
        }

        try
        {
            var service = new ExplainService(dataSource);

            // CREATE TABLE AS is one of the few statements EXPLAIN accepts that
            // also creates something; it creates nothing under plain EXPLAIN,
            // and the always-rolled-back transaction keeps it that way on
            // every path.
            var run = await service.ExplainAsync(
                $"CREATE TABLE {ScratchTable} AS SELECT 1 AS id", analyze: false, CancellationToken.None);
            await Assert.That(run.Result.Root).IsNotNull();
            await Assert.That(await TableExistsAsync(dataSource)).IsFalse();

            // And ANALYZE of the same statement really runs it, inside the
            // transaction that is rolled back.
            run = await service.ExplainAsync(
                $"CREATE TABLE {ScratchTable} AS SELECT 1 AS id", analyze: true, CancellationToken.None);
            await Assert.That(run.Result.Root).IsNotNull();
            await Assert.That(await TableExistsAsync(dataSource)).IsFalse();
        }
        finally
        {
            await using var cleanup = await dataSource.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {ScratchTable}", cleanup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("name", ScratchTable);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
