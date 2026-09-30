using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Safe mode's optimistic concurrency check against a real server, with a real
/// second session doing the interfering. The pure comparison is covered by
/// <see cref="StagedRowCheckTests"/>; what only a server can show is that a
/// conflict rolls back the *whole* batch (including the rows that were fine),
/// that a row locked by an open transaction fails in seconds rather than
/// hanging, and that a conflict inside the user's own transaction undoes only
/// the batch.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like <see cref="QueryEngineCompositeTests"/>.
/// </summary>
[NotInParallel]
public class QueryEngineStagedConflictTests
{
    private const string Table = "pgnimbus_staged_conflict_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to test staged-change conflicts against.");
        }
    }

    // A composite key, a nullable column and a plain one: the three shapes the
    // check has to get right.
    private static async Task<NpgsqlDataSource> SeedAsync()
    {
        var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await ExecAsync(dataSource, $"""
            DROP TABLE IF EXISTS {Table};
            CREATE TABLE {Table} (order_id int, line int, qty int NOT NULL, note text, PRIMARY KEY (order_id, line));
            INSERT INTO {Table} VALUES (1, 1, 2, NULL), (1, 2, 3, 'gift'), (2, 1, 5, NULL);
            """);
        return dataSource;
    }

    private static async Task ExecAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static RowSnapshot Row(int orderId, int line, int qty, string? note) =>
        new(["order_id", "line", "qty", "note"], [orderId, line, qty, note]);

    private static PendingChangeSet StageTwoRows()
    {
        var set = new PendingChangeSet("public", Table, ["order_id", "line"]);
        set.StageEdit([1, 1], "qty", 20, original: Row(1, 1, 2, null));
        set.StageDelete([2, 1], Row(2, 1, 5, null));
        return set;
    }

    private static Task<int> CommitAsync(QueryEngine engine, PendingChangeSet set) =>
        engine.ApplyBatchAsync(set.BuildStatements(), set.BuildRowCheck(), CancellationToken.None);

    [Test]
    public async Task UntouchedRowsCommit()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();

        var affected = await CommitAsync(new QueryEngine(dataSource), StageTwoRows());

        await Assert.That(affected).IsEqualTo(2);
        await Assert.That(await ScalarAsync(dataSource, $"SELECT qty FROM {Table} WHERE order_id = 1 AND line = 1")).IsEqualTo(20);
        await Assert.That(await ScalarAsync(dataSource, $"SELECT count(*) FROM {Table} WHERE order_id = 2")).IsEqualTo(0L);
    }

    [Test]
    public async Task AnotherSessionSettingANullColumnRollsBackTheWholeBatch()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        var set = StageTwoRows();

        // The other session touches a column nobody staged a value for.
        await ExecAsync(dataSource, $"UPDATE {Table} SET note = 'rush' WHERE order_id = 1 AND line = 1");

        var ex = await Assert.That(() => CommitAsync(new QueryEngine(dataSource), set)).Throws<StagedChangesConflictException>();

        await Assert.That(ex!.Conflicts).Count().IsEqualTo(1);
        await Assert.That(ex.Conflicts[0].KeyText).IsEqualTo("order_id = 1, line = 1");
        await Assert.That(ex.Conflicts[0].Columns.Single(c => c.Column == "note").Current).IsEqualTo("rush");

        // The delete of (2,1) was fine on its own, and still didn't happen.
        await Assert.That(await ScalarAsync(dataSource, $"SELECT count(*) FROM {Table} WHERE order_id = 2")).IsEqualTo(1L);
        await Assert.That(await ScalarAsync(dataSource, $"SELECT qty FROM {Table} WHERE order_id = 1 AND line = 1")).IsEqualTo(2);
    }

    [Test]
    public async Task AnotherSessionDeletingAStagedRowRollsBackTheWholeBatch()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        var set = StageTwoRows();

        await ExecAsync(dataSource, $"DELETE FROM {Table} WHERE order_id = 2 AND line = 1");

        var ex = await Assert.That(() => CommitAsync(new QueryEngine(dataSource), set)).Throws<StagedChangesConflictException>();

        await Assert.That(ex!.Conflicts.Single().Kind).IsEqualTo(RowConflictKind.Deleted);
        await Assert.That(await ScalarAsync(dataSource, $"SELECT qty FROM {Table} WHERE order_id = 1 AND line = 1")).IsEqualTo(2);
    }

    [Test]
    public async Task RebasingAfterAConflictLetsTheNextCommitThrough()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        var engine = new QueryEngine(dataSource);
        var set = StageTwoRows();
        await ExecAsync(dataSource, $"UPDATE {Table} SET note = 'rush' WHERE order_id = 1 AND line = 1");

        var ex = await Assert.That(() => CommitAsync(engine, set)).Throws<StagedChangesConflictException>();
        set.Rebase(ex!.Conflicts);
        await CommitAsync(engine, set);

        await Assert.That(await ScalarAsync(dataSource, $"SELECT qty || '/' || note FROM {Table} WHERE order_id = 1 AND line = 1")).IsEqualTo("20/rush");
    }

    [Test]
    public async Task ARowLockedByAnOpenTransactionFailsInsteadOfHanging()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        var set = StageTwoRows();

        await using var holder = await dataSource.OpenConnectionAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"UPDATE {Table} SET qty = qty WHERE order_id = 2 AND line = 1", holder, holding))
        {
            await command.ExecuteNonQueryAsync();
        }

        var started = DateTime.UtcNow;
        var ex = await Assert.That(() => CommitAsync(new QueryEngine(dataSource), set)).Throws<StagedChangesConflictException>();

        await Assert.That(ex!.RowsLocked).IsTrue();
        await Assert.That(DateTime.UtcNow - started).IsLessThan(StagedRowCheck.LockTimeout + TimeSpan.FromSeconds(10));
        await holding.RollbackAsync();
        await Assert.That(await ScalarAsync(dataSource, $"SELECT qty FROM {Table} WHERE order_id = 1 AND line = 1")).IsEqualTo(2);
    }

    [Test]
    public async Task InsideAnExplicitTransactionAConflictUndoesOnlyTheBatch()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        var engine = new QueryEngine(dataSource);
        var set = StageTwoRows();
        await ExecAsync(dataSource, $"UPDATE {Table} SET qty = 99 WHERE order_id = 1 AND line = 1");

        await engine.BeginTransactionAsync(CancellationToken.None);
        await engine.ExecuteNonQueryAsync(
            $"UPDATE {Table} SET note = 'mine' WHERE order_id = 1 AND line = 2",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        await Assert.That(() => CommitAsync(engine, set)).Throws<StagedChangesConflictException>();

        await Assert.That(engine.IsInTransaction).IsTrue();
        var lockTimeout = await engine.ExecuteAsync("SHOW lock_timeout", CancellationToken.None);
        await engine.CommitAsync(CancellationToken.None);

        await Assert.That(await ScalarAsync(dataSource, $"SELECT note FROM {Table} WHERE order_id = 1 AND line = 2")).IsEqualTo("mine");
        await Assert.That(await ScalarAsync(dataSource, $"SELECT count(*) FROM {Table} WHERE order_id = 2")).IsEqualTo(1L);
        await Assert.That(lockTimeout).IsTypeOf<MaterializedResultSet>();
        await Assert.That(((MaterializedResultSet)lockTimeout).Rows[0][0]).IsEqualTo("0");
    }
}
