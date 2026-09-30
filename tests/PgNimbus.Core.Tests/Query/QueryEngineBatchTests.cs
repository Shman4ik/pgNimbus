using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Staged statements go to the server <see cref="QueryEngine.StatementsPerBatch"/>
/// to a round trip. What must not change with that is the batch's promise: it is
/// all or nothing, and a statement that touches other than its expected row count
/// undoes everything, whichever round trip it was in. Gated on
/// <c>PGNIMBUS_TEST_CONN</c> like the other live engine tests.
/// </summary>
[NotInParallel]
public class QueryEngineBatchTests
{
    private const string Table = "pgnimbus_batch_scratch";

    // More than two round trips' worth, so a failure can land in a later one.
    private const int Rows = QueryEngine.StatementsPerBatch * 2 + 100;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to test batches against.");
        }
    }

    private static async Task ExecAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static List<ParameterizedStatement> Updates(int count, int missingAt = -1) =>
    [
        .. Enumerable.Range(1, count).Select(i => new ParameterizedStatement(
            $"UPDATE {Table} SET qty = qty + 1 WHERE id = @id",
            new Dictionary<string, object?> { ["id"] = i == missingAt ? -1 : i },
            ExpectedRowsAffected: 1)),
    ];

    private static async Task<NpgsqlDataSource> SeedAsync()
    {
        var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await ExecAsync(dataSource, $"""
            DROP TABLE IF EXISTS {Table};
            CREATE TABLE {Table} (id int PRIMARY KEY, qty int NOT NULL);
            INSERT INTO {Table} SELECT g, 0 FROM generate_series(1, {Rows}) g;
            """);
        return dataSource;
    }

    [Test]
    public async Task Every_statement_of_a_multi_round_trip_batch_runs_once()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        try
        {
            var affected = await new QueryEngine(dataSource).ApplyBatchAsync(Updates(Rows), CancellationToken.None);

            await Assert.That(affected).IsEqualTo(Rows);
            await Assert.That(await ScalarAsync(dataSource, $"SELECT sum(qty) FROM {Table}")).IsEqualTo((long)Rows);
            await Assert.That(await ScalarAsync(dataSource, $"SELECT count(*) FROM {Table} WHERE qty <> 1")).IsEqualTo(0L);
        }
        finally
        {
            await ExecAsync(dataSource, $"DROP TABLE IF EXISTS {Table}");
        }
    }

    [Test]
    public async Task A_row_count_miss_in_a_later_round_trip_undoes_the_earlier_ones()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        try
        {
            var engine = new QueryEngine(dataSource);

            await Assert.That(async () => await engine.ApplyBatchAsync(Updates(Rows, missingAt: Rows - 10), CancellationToken.None))
                .Throws<StagedChangesConflictException>();
            await Assert.That(await ScalarAsync(dataSource, $"SELECT sum(qty) FROM {Table}")).IsEqualTo(0L);
        }
        finally
        {
            await ExecAsync(dataSource, $"DROP TABLE IF EXISTS {Table}");
        }
    }

    [Test]
    public async Task Inside_the_users_transaction_a_miss_undoes_only_the_batch()
    {
        SkipIfNoConnection();
        await using var dataSource = await SeedAsync();
        try
        {
            var engine = new QueryEngine(dataSource);
            await engine.BeginTransactionAsync(CancellationToken.None);
            await engine.ExecuteNonQueryAsync($"UPDATE {Table} SET qty = 100 WHERE id = 1", new Dictionary<string, object?>(), CancellationToken.None);

            await Assert.That(async () => await engine.ApplyBatchAsync(Updates(Rows, missingAt: Rows - 10), CancellationToken.None))
                .Throws<StagedChangesConflictException>();

            await engine.CommitAsync(CancellationToken.None);

            // The user's own UPDATE survived; nothing from the batch did.
            await Assert.That(await ScalarAsync(dataSource, $"SELECT sum(qty) FROM {Table}")).IsEqualTo(100L);
        }
        finally
        {
            await ExecAsync(dataSource, $"DROP TABLE IF EXISTS {Table}");
        }
    }
}
