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
    public async Task ScriptRetriesOnlyItsFirstStatementAfterConnectionLoss()
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
        foreach (var result in results)
        {
            await Assert.That(result).IsTypeOf<MaterializedResultSet>();
        }
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
    }
}
