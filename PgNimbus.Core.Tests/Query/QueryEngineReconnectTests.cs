using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Exercises auto-reconnect against a real Postgres server: these tests kill
/// live backends server-side (<c>pg_terminate_backend</c>) to simulate the
/// dead-socket condition a laptop sleep or a dropped SSH tunnel leaves
/// behind, then assert <see cref="QueryEngine"/> quietly recovers — or, for
/// the held transaction connection, surfaces a clear "transaction lost"
/// state instead of hanging or throwing a raw <see cref="NpgsqlException"/>.
/// The second half is the 2026-09 security audit's live check (finding 2): a
/// statement terminated <em>while it runs</em> is reported with its outcome
/// unknown and never sent again, whichever engine path ran it.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c>: unset (the default for a plain local
/// `dotnet test`), every test in this class skips cleanly. CI's
/// <c>postgres:17</c> service container sets it so these actually run there.
/// </summary>
// pg_terminate_backend kills every backend on the test database except its
// own admin connection — that would clobber any other test hitting the same
// database concurrently, so the whole class is serialized.
[NotInParallel]
public class QueryEngineReconnectTests
{
    private const string ScratchTable = "pgnimbus_reconnect_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to test auto-reconnect against.");
        }
    }

    private static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString!);

    // The sanctioned way to simulate a dead pooled connection without
    // actually pulling a cable or sleeping the machine: kill every backend
    // for the test database from a separate admin connection (so the admin
    // query itself isn't one of the backends it kills), then give Postgres a
    // moment to actually close the sockets — pg_terminate_backend only
    // *signals* the target backend, it doesn't block until it's gone.
    private static async Task KillBackendsAsync()
    {
        await using var admin = CreateDataSource();
        await using var connection = await admin.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
              FROM pg_stat_activity
             WHERE pid <> pg_backend_pid()
               AND datname = current_database()
            """,
            connection);
        await command.ExecuteNonQueryAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
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
    public async Task SingleDeadPooledConnectionIsTransparentlyReconnected()
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        var engine = new QueryEngine(dataSource);

        // Warm exactly one pooled connection, then kill it out from under the
        // pool. Without the feature this reproduces the bug verbatim: Npgsql
        // hands the retry a socket that looks idle but is actually dead, and
        // the query either throws or hangs instead of streaming a row.
        await DrainAsync(await engine.ExecuteAsync("SELECT 1", CancellationToken.None));
        await KillBackendsAsync();

        var result = await engine.ExecuteAsync("SELECT 42", CancellationToken.None);

        await Assert.That(result).IsTypeOf<ResultSet>();
        var rows = await DrainAsync(result);

        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0][0]).IsEqualTo(42);
    }

    [Test]
    public async Task TwoDeadPooledConnectionsAreBothFlushedByTheRetry()
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        var engine = new QueryEngine(dataSource);

        // pg_sleep keeps both connections checked out at the same time, so
        // the pool ends up holding two distinct idle (and, after the kill,
        // dead) connections — proof that clearing the whole pool, not just
        // retrying once on whichever single connection Npgsql happened to
        // hand back, is what makes the next query succeed.
        var first = engine.ExecuteAsync("SELECT pg_sleep(0.2), 1", CancellationToken.None);
        var second = engine.ExecuteAsync("SELECT pg_sleep(0.2), 2", CancellationToken.None);

        await DrainAsync(await first);
        await DrainAsync(await second);

        await KillBackendsAsync();

        var result = await engine.ExecuteAsync("SELECT 99", CancellationToken.None);
        var rows = await DrainAsync(result);

        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0][0]).IsEqualTo(99);
    }

    [Test]
    public async Task TransactionLostOnConnectionDropSurfacesClearErrorAndRecovers()
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        var engine = new QueryEngine(dataSource);

        var stateChanges = 0;
        engine.TransactionStateChanged += () => Interlocked.Increment(ref stateChanges);

        await engine.BeginTransactionAsync(CancellationToken.None);
        await KillBackendsAsync();

        var result = await engine.ExecuteAsync("SELECT 1", CancellationToken.None);

        await Assert.That(result).IsTypeOf<QueryError>();
        var error = (QueryError)result;

        await Assert.That(error.RolledBack).IsTrue();
        await Assert.That(error.ConnectionLost).IsTrue();
        await Assert.That(engine.IsInTransaction).IsFalse();
        // BEGIN fired one change, the loss fired a second.
        await Assert.That(stateChanges).IsGreaterThanOrEqualTo(2);

        // The engine reconnects on its own for the next statement — no
        // lingering "stuck" state from the lost transaction.
        var followUp = await engine.ExecuteAsync("SELECT 1", CancellationToken.None);
        var rows = await DrainAsync(followUp);
        await Assert.That(rows).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ScriptRecoversFromLossBeforeFirstStatement()
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        var engine = new QueryEngine(dataSource);

        await DrainAsync(await engine.ExecuteAsync("SELECT 1", CancellationToken.None));
        await KillBackendsAsync();

        var results = new List<StatementResult>();
        await foreach (var result in engine.ExecuteScriptAsync(["SELECT 1", "SELECT 2"], null, CancellationToken.None))
        {
            results.Add(result);
        }

        await Assert.That(results).Count().IsEqualTo(2);
        await Assert.That(results[0]).IsTypeOf<MaterializedResultSet>();
        await Assert.That(results[1]).IsTypeOf<MaterializedResultSet>();
        await Assert.That(((MaterializedResultSet)results[0]).Rows[0][0]).IsEqualTo(1);
        await Assert.That(((MaterializedResultSet)results[1]).Rows[0][0]).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteNonQueryDoesNotThrowAfterConnectionLoss()
    {
        SkipIfNoConnection();

        await using var dataSource = CreateDataSource();
        var engine = new QueryEngine(dataSource);

        await DrainAsync(await engine.ExecuteAsync("SELECT 1", CancellationToken.None));
        await KillBackendsAsync();

        // Must complete without throwing — the whole point of the retry-once
        // is that this dead-socket case never reaches the caller as an
        // exception.
        await engine.ExecuteNonQueryAsync("SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);
    }

    [Test]
    public async Task ApplyBatchRetriesTheWholeBatchAfterConnectionLoss()
    {
        SkipIfNoConnection();

        await using (var admin = CreateDataSource())
        await using (var setup = await admin.OpenConnectionAsync())
        {
            await using var createTable = new NpgsqlCommand(
                $"""CREATE TABLE IF NOT EXISTS {ScratchTable} (id integer PRIMARY KEY, val integer NOT NULL)""",
                setup);
            await createTable.ExecuteNonQueryAsync();

            await using var seed = new NpgsqlCommand(
                $"""INSERT INTO {ScratchTable} (id, val) VALUES (1, 0) ON CONFLICT (id) DO UPDATE SET val = 0""",
                setup);
            await seed.ExecuteNonQueryAsync();
        }

        await using var dataSource = CreateDataSource();
        var engine = new QueryEngine(dataSource);

        await DrainAsync(await engine.ExecuteAsync("SELECT 1", CancellationToken.None));
        await KillBackendsAsync();

        var statements = new[]
        {
            new ParameterizedStatement(
                $"""UPDATE {ScratchTable} SET val = val + 1 WHERE id = @id""",
                new Dictionary<string, object?> { ["id"] = 1 }),
        };

        var affected = await engine.ApplyBatchAsync(statements, CancellationToken.None);

        await Assert.That(affected).IsEqualTo(1);
        var rows = await DrainAsync(await engine.ExecuteAsync($"SELECT val FROM {ScratchTable} WHERE id = 1", CancellationToken.None));
        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0][0]).IsEqualTo(1);
    }

    // --- A statement killed while it runs is reported, never re-sent ----------

    private const string KillTable = "pgnimbus_reconnect_kill";

    // The audit's reproduction: an INSERT that takes long enough to be found
    // in pg_stat_activity and terminated from a second session. The old
    // retry re-sent it and the row appeared with no error shown.
    private static string SlowInsert => $"INSERT INTO {KillTable} SELECT 1 FROM pg_sleep(3)";

    private static async Task CreateKillTableAsync()
    {
        await using var admin = CreateDataSource();
        await using var connection = await admin.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP TABLE IF EXISTS {KillTable}; CREATE TABLE {KillTable} (n int)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropKillTableAsync()
    {
        await using var admin = CreateDataSource();
        await using var connection = await admin.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {KillTable}", connection);
        await command.ExecuteNonQueryAsync();
    }

    // Waits until the slow INSERT shows up as running, then terminates exactly
    // that backend — the DBA's gesture, not a blanket kill. "Running" means
    // parked in pg_sleep: the engine's describe step (Parse/Describe, nothing
    // executed) already shows the text as an active query, and a kill landing
    // there is a loss before the send, which the engine rightly retries, so
    // the script test saw the INSERT finish on the retry and then SELECT 2.
    private static async Task TerminateSlowInsertAsync()
    {
        await using var admin = CreateDataSource();
        await using var connection = await admin.OpenConnectionAsync();
        for (var i = 0; i < 100; i++)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT pg_terminate_backend(pid)
                  FROM pg_stat_activity
                 WHERE pid <> pg_backend_pid()
                   AND datname = current_database()
                   AND state = 'active'
                   AND wait_event = 'PgSleep'
                   AND query LIKE '%pg_sleep(3)%'
                   AND query NOT LIKE '%pg_stat_activity%'
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new InvalidOperationException("The slow INSERT never showed up as running.");
    }

    private static async Task<long> KillTableRowsAsync()
    {
        await using var admin = CreateDataSource();
        await using var connection = await admin.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {KillTable}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Test]
    public async Task AStatementTerminatedWhileRunningIsReportedNotReRun()
    {
        SkipIfNoConnection();

        await CreateKillTableAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            var run = engine.ExecuteAsync(SlowInsert, CancellationToken.None);
            await TerminateSlowInsertAsync();
            var result = await run;

            await Assert.That(result).IsTypeOf<QueryError>();
            var error = (QueryError)result;
            await Assert.That(error.ConnectionLost).IsTrue();
            await Assert.That(error.OutcomeUnknown).IsTrue();
            await Assert.That(error.SqlState).IsEqualTo(PostgresErrorCodes.AdminShutdown);
            await Assert.That(error.Message).Contains("was not run again");

            // The whole point: the DBA's kill stands.
            await Assert.That(await KillTableRowsAsync()).IsEqualTo(0L);

            // And the engine is not stuck: the next statement reconnects.
            var rows = await DrainAsync(await engine.ExecuteAsync("SELECT 7", CancellationToken.None));
            await Assert.That(rows[0][0]).IsEqualTo(7);
        }
        finally
        {
            await DropKillTableAsync();
        }
    }

    [Test]
    public async Task AParameterizedStatementTerminatedWhileRunningThrowsAndIsNotReRun()
    {
        SkipIfNoConnection();

        await CreateKillTableAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            // ExecuteNonQueryAsync is what the Add-row dialog's INSERT goes
            // through; it used to retry on the same classifier as the rest.
            var run = engine.ExecuteNonQueryAsync(
                $"INSERT INTO {KillTable} SELECT @n FROM pg_sleep(3)",
                new Dictionary<string, object?> { ["n"] = 1 },
                CancellationToken.None);
            await TerminateSlowInsertAsync();

            // Reported as "may or may not have taken effect", not as a plain
            // failure: an Add-row that says "failed" invites a second INSERT.
            var thrown = await Assert.ThrowsAsync<StatementOutcomeUnknownException>(async () => await run);
            await Assert.That(thrown!.InnerException).IsTypeOf<PostgresException>();
            await Assert.That(((PostgresException)thrown.InnerException!).SqlState).IsEqualTo(PostgresErrorCodes.AdminShutdown);
            await Assert.That(thrown.Message).Contains("may or may not have taken effect");
            await Assert.That(await KillTableRowsAsync()).IsEqualTo(0L);
        }
        finally
        {
            await DropKillTableAsync();
        }
    }

    [Test]
    public async Task AScriptsFirstStatementTerminatedWhileRunningIsNotReRun()
    {
        SkipIfNoConnection();

        await CreateKillTableAsync();
        await using var dataSource = CreateDataSource();
        try
        {
            var engine = new QueryEngine(dataSource);

            // The script path retried its first statement on any loss; now only
            // one that never went out.
            var results = new List<StatementResult>();
            var run = Task.Run(async () =>
            {
                await foreach (var result in engine.ExecuteScriptAsync([SlowInsert, "SELECT 2"], null, CancellationToken.None))
                {
                    results.Add(result);
                }
            });
            await TerminateSlowInsertAsync();
            await run;

            await Assert.That(results).Count().IsEqualTo(1);
            var error = (QueryError)results[0];
            await Assert.That(error.ConnectionLost).IsTrue();
            await Assert.That(error.OutcomeUnknown).IsTrue();
            await Assert.That(await KillTableRowsAsync()).IsEqualTo(0L);
        }
        finally
        {
            await DropKillTableAsync();
        }
    }
}
