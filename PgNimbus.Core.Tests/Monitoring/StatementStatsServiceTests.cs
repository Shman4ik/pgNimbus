using Npgsql;
using PgNimbus.Core.Monitoring;

namespace PgNimbus.Core.Tests.Monitoring;

/// <summary>
/// pg_stat_statements against a real server. Gated on <c>PGNIMBUS_TEST_CONN</c>,
/// and split by what that server can do: CI's plain <c>postgres:17</c> doesn't
/// preload the library, which is exactly the setup that tests the two
/// "unavailable" answers; a server started with
/// <c>shared_preload_libraries=pg_stat_statements</c> tests the reads. Each test
/// skips on the other kind, so between the two every path runs somewhere.
/// </summary>
[NotInParallel]
public class StatementStatsServiceTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private const string LowRole = "pgnimbus_stats_reader";

    private static async Task<NpgsqlDataSource> ServerAsync(bool needsPreload)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read pg_stat_statements from.");
        }

        var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var show = dataSource.CreateCommand("SHOW shared_preload_libraries");
        var preloaded = ((string)(await show.ExecuteScalarAsync())!).Contains("pg_stat_statements", StringComparison.Ordinal);
        if (preloaded != needsPreload)
        {
            await dataSource.DisposeAsync();
            Skip.Test(needsPreload
                ? "The test server doesn't preload pg_stat_statements; the reading tests need one that does."
                : "The test server preloads pg_stat_statements; this checks a server that doesn't.");
        }

        return dataSource;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static StatementStat Workload(StatementStatsSnapshot snapshot) =>
        snapshot.Statements.Single(s => s.Query.Contains("pgnimbus_workload", StringComparison.Ordinal));

    // Three calls of one statement that differ only in a literal: one entry.
    private static async Task RunWorkloadAsync(NpgsqlDataSource dataSource, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await ExecuteAsync(dataSource, $"SELECT count(*) AS pgnimbus_workload FROM generate_series(1, 2000) g WHERE g > {i}");
        }
    }

    [Test]
    public async Task Without_the_extension_it_says_it_is_not_installed()
    {
        await using var dataSource = await ServerAsync(needsPreload: false);
        await ExecuteAsync(dataSource, "DROP EXTENSION IF EXISTS pg_stat_statements");

        var read = await new StatementStatsService(dataSource).ReadAsync(CancellationToken.None);
        await Assert.That(read.Problem).IsEqualTo(StatementStatsProblem.NotInstalled);
        await Assert.That(read.Snapshot).IsNull();
    }

    [Test]
    public async Task With_the_extension_but_not_preloaded_it_says_it_is_not_loaded()
    {
        await using var dataSource = await ServerAsync(needsPreload: false);
        await ExecuteAsync(dataSource, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
        try
        {
            var read = await new StatementStatsService(dataSource).ReadAsync(CancellationToken.None);
            await Assert.That(read.Problem).IsEqualTo(StatementStatsProblem.NotLoaded);
        }
        finally
        {
            await ExecuteAsync(dataSource, "DROP EXTENSION IF EXISTS pg_stat_statements");
        }
    }

    [Test]
    public async Task It_reads_normalized_statements_and_an_interval_counts_only_what_ran_in_between()
    {
        await using var dataSource = await ServerAsync(needsPreload: true);
        await ExecuteAsync(dataSource, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
        await ExecuteAsync(dataSource, "SELECT pg_stat_statements_reset()");
        var service = new StatementStatsService(dataSource);

        await RunWorkloadAsync(dataSource, 3);
        var first = (await service.ReadAsync(CancellationToken.None)).Snapshot!;
        var workload = Workload(first);
        await Assert.That(workload.Calls).IsEqualTo(3L);
        await Assert.That(workload.Query).Contains("$1");
        await Assert.That(workload.TotalMs).IsGreaterThan(0d);
        await Assert.That(first.StatsReset).IsNotNull();

        await RunWorkloadAsync(dataSource, 2);
        var second = (await service.ReadAsync(CancellationToken.None)).Snapshot!;
        var interval = StatementStatsInterval.Between(first, second);
        var ran = interval.Activity.Single(a => a.Statement.Key == workload.Key);
        await Assert.That(ran.Calls).IsEqualTo(2L);
        await Assert.That(ran.Restarted).IsFalse();

        // The whole text, for opening it in the editor.
        var text = await service.GetQueryTextAsync(workload, CancellationToken.None);
        await Assert.That(text).StartsWith("SELECT count(*) AS pgnimbus_workload");

        // A reset in between is reported, and the counts start over from it.
        await ExecuteAsync(dataSource, "SELECT pg_stat_statements_reset()");
        await RunWorkloadAsync(dataSource, 1);
        var third = (await service.ReadAsync(CancellationToken.None)).Snapshot!;
        var afterReset = StatementStatsInterval.Between(second, third);
        await Assert.That(afterReset.ViewReset).IsTrue();
        await Assert.That(afterReset.Activity.Single(a => a.Statement.Key == workload.Key).Calls).IsEqualTo(1L);
    }

    [Test]
    public async Task Another_roles_statements_are_counted_as_hidden_without_pg_read_all_stats()
    {
        await using var dataSource = await ServerAsync(needsPreload: true);
        await ExecuteAsync(dataSource, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
        await ExecuteAsync(dataSource, $"DROP ROLE IF EXISTS {LowRole}; CREATE ROLE {LowRole} LOGIN PASSWORD 'reader'");
        try
        {
            await RunWorkloadAsync(dataSource, 1);
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = LowRole, Password = "reader" };
            await using var lowDataSource = NpgsqlDataSource.Create(builder.ConnectionString);

            var read = await new StatementStatsService(lowDataSource).ReadAsync(CancellationToken.None);
            await Assert.That(read.Problem).IsEqualTo(StatementStatsProblem.None);
            await Assert.That(read.Snapshot!.HiddenStatements).IsGreaterThan(0);
            await Assert.That(read.Snapshot.Statements.Any(s => s.Query.Contains("pgnimbus_workload", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            await ExecuteAsync(dataSource, $"DROP ROLE IF EXISTS {LowRole}");
        }
    }
}
