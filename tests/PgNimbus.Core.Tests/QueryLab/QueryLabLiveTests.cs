using Npgsql;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>
/// The issue's live half of "done when": two explicit runs against a real server, an
/// index created between them, compared. Gated on <c>PGNIMBUS_TEST_CONN</c> like the
/// engine's integration tests.
/// </summary>
[NotInParallel]
public class QueryLabLiveTests
{
    private const string Table = "pgnimbus_query_lab_scratch";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Two_live_runs_around_a_new_index_compare_as_a_changed_scan()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to explain against.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await ExecuteAsync(dataSource,
            $"DROP TABLE IF EXISTS {Table}",
            $"CREATE TABLE {Table} (id int PRIMARY KEY, k int NOT NULL, v text NOT NULL)",
            $"INSERT INTO {Table} SELECT g, g % 5000, md5(g::text) FROM generate_series(1, 200000) g",
            $"ANALYZE {Table}");
        try
        {
            var service = new ExplainService(dataSource);
            var statement = $"SELECT count(*), max(v) FROM {Table} WHERE k = 42";

            var before = await service.ExplainAsync(statement, analyze: true, CancellationToken.None);
            await ExecuteAsync(dataSource, $"CREATE INDEX {Table}_k_idx ON {Table} (k)", $"ANALYZE {Table}");
            var after = await service.ExplainAsync(statement, analyze: true, CancellationToken.None);

            var comparison = PlanComparison.Compare(
                new PlanSide(before.Result, before.ServerVersion, "test", statement),
                new PlanSide(after.Result, after.ServerVersion, "test", statement));
            var scan = Plans.All(comparison.Diff.Roots).Single(n => n.After?.RelationName == Table);

            await Assert.That(before.ServerVersion).IsNotNull();
            await Assert.That(scan.Before!.NodeType).IsEqualTo("Seq Scan");
            await Assert.That(scan.After!.NodeType).IsNotEqualTo("Seq Scan");
            await Assert.That(scan.Change).IsEqualTo(PlanNodeChange.Changed);
            await Assert.That(comparison.Notes.Select(n => n.Title)).Contains("One run each");
            await Assert.That(comparison.Notes.Select(n => n.Title)).DoesNotContain("Different statements");
            await Assert.That(comparison.Metrics.Single(r => r.Label == "Rows returned").ChangeText).IsEqualTo("no change");
        }
        finally
        {
            await ExecuteAsync(dataSource, $"DROP TABLE IF EXISTS {Table}");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, params string[] statements)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        foreach (var sql in statements)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
