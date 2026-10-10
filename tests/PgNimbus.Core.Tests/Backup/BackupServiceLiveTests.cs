using Npgsql;
using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// Real backups with a real pg_dump against a real server (see
/// <see cref="LiveBackupDatabase"/> for the gating). Each checks what the
/// archive holds through <c>pg_restore --list</c>, so the scope, the exact
/// name matching and the partitions are proven by pg_dump's own output, and
/// the failure paths check the one promise a backup tool must not break: an
/// older backup at the same path is never destroyed by a newer one that failed.
/// </summary>
[NotInParallel(nameof(LiveBackupDatabase))]
public class BackupServiceLiveTests
{
    private static async Task<(BackupService Service, PgToolInstall Tool, NpgsqlDataSource Source)> SetUpAsync()
    {
        LiveBackupDatabase.SkipIfNoServer();
        await LiveBackupDatabase.CreateAsync(LiveBackupDatabase.Name, LiveBackupDatabase.ShopSeed);
        var connectionString = LiveBackupDatabase.ConnectionStringFor(LiveBackupDatabase.Name);
        var source = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(source, PgToolConnection.FromConnectionString(connectionString));
        var tool = await LiveBackupDatabase.RequireToolsAsync(await service.GetServerVersionAsync(CancellationToken.None));
        return (service, tool, source);
    }

    private static string TempFile(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "backup" + extension);
    }

    [Test]
    public async Task A_database_backup_holds_every_table_and_reports_its_progress()
    {
        var (service, tool, source) = await SetUpAsync();
        await using var _ = source;
        var path = TempFile(".dump");
        var reports = new List<BackupProgress>();

        var result = await service.RunAsync(
            tool,
            new BackupPlan(BackupScope.Database, BackupContent.Everything, path),
            new SynchronousProgress<BackupProgress>(reports.Add),
            CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(BackupOutcome.Succeeded).Because(result.Error ?? result.Log);
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(File.Exists(path + ".partial")).IsFalse();
        await Assert.That(result.Bytes).IsEqualTo(new FileInfo(path).Length);
        await Assert.That(result.TablesSaved).IsEqualTo(4);
        await Assert.That(reports.Any(r => r.Stage == BackupStage.SavingData)).IsTrue();
        await Assert.That(reports[^1].Fraction).IsEqualTo(1d);

        var list = await LiveBackupDatabase.ListAsync(tool, path);
        await Assert.That(list).Contains("TABLE DATA public customers");
        await Assert.That(list).Contains("TABLE DATA sales orders");
        await Assert.That(list).Contains("TABLE DATA sales events_2026");
        await Assert.That(list).Contains("TABLE DATA Odd \"Schema\" *? Weird.Table");
        await Assert.That(list).Contains("MATERIALIZED VIEW DATA sales order_totals");
    }

    [Test]
    public async Task A_schema_backup_holds_that_schema_alone_however_it_is_named()
    {
        var (service, tool, source) = await SetUpAsync();
        await using var _ = source;
        var path = TempFile(".dump");

        var result = await service.RunAsync(
            tool, new BackupPlan(BackupScope.ForSchema(LiveBackupDatabase.OddSchema), BackupContent.Everything, path), null, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(BackupOutcome.Succeeded).Because(result.Error ?? result.Log);
        var list = await LiveBackupDatabase.ListAsync(tool, path);
        await Assert.That(list).Contains("TABLE DATA Odd \"Schema\" *? Weird.Table");
        await Assert.That(list).DoesNotContain("customers");
        await Assert.That(list).DoesNotContain("orders");
    }

    [Test]
    public async Task A_partitioned_table_backup_holds_its_partitions_rows()
    {
        var (service, tool, source) = await SetUpAsync();
        await using var _ = source;
        var path = TempFile(".dump");

        var result = await service.RunAsync(
            tool, new BackupPlan(BackupScope.ForTable("sales", "events", isPartitioned: true), BackupContent.Everything, path), null, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(BackupOutcome.Succeeded).Because(result.Error ?? result.Log);
        await Assert.That(result.TablesSaved).IsEqualTo(1);
        var list = await LiveBackupDatabase.ListAsync(tool, path);
        await Assert.That(list).Contains("TABLE sales events ");
        await Assert.That(list).Contains("TABLE DATA sales events_2026");
        await Assert.That(list).DoesNotContain("orders");
    }

    [Test]
    public async Task A_structure_only_script_has_the_definitions_and_no_rows()
    {
        var (service, tool, source) = await SetUpAsync();
        await using var _ = source;
        var path = TempFile(".sql");

        var result = await service.RunAsync(
            tool, new BackupPlan(BackupScope.Database, BackupContent.StructureOnly, path), null, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(BackupOutcome.Succeeded).Because(result.Error ?? result.Log);
        var script = await File.ReadAllTextAsync(path);
        await Assert.That(script).Contains("CREATE TABLE sales.orders");
        await Assert.That(script).Contains("CREATE TABLE \"Odd \"\"Schema\"\" *?\".\"Weird.Table\"");
        await Assert.That(script).DoesNotContain("COPY ");
    }

    [Test]
    public async Task A_failed_backup_leaves_the_older_file_untouched_and_says_why()
    {
        var (service, tool, source) = await SetUpAsync();
        await using var _ = source;
        var path = TempFile(".dump");
        await File.WriteAllTextAsync(path, "yesterday's backup");
        var wrongPassword = new BackupService(source, service.Connection with { Password = "not the password" });

        var result = await wrongPassword.RunAsync(
            tool, new BackupPlan(BackupScope.Database, BackupContent.Everything, path), null, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(BackupOutcome.Failed);
        await Assert.That(result.Error).Contains("password authentication failed");
        await Assert.That(result.Hint).IsNotNull();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("yesterday's backup");
        await Assert.That(File.Exists(path + ".partial")).IsFalse();
    }

    [Test]
    public async Task Stopping_a_backup_ends_pg_dump_and_keeps_the_older_file()
    {
        var (service, tool, source) = await SetUpAsync();
        await using var _ = source;
        var path = TempFile(".dump");
        await File.WriteAllTextAsync(path, "yesterday's backup");

        // pg_dump takes a share lock on every table it saves; holding an
        // exclusive one keeps it waiting for as long as the test wants.
        await using var blocker = await source.OpenConnectionAsync();
        await using (var begin = new NpgsqlCommand("BEGIN; LOCK TABLE sales.orders IN ACCESS EXCLUSIVE MODE", blocker))
        {
            await begin.ExecuteNonQueryAsync();
        }

        using var stop = new CancellationTokenSource();
        var running = service.RunAsync(tool, new BackupPlan(BackupScope.Database, BackupContent.Everything, path), null, stop.Token);
        await WaitForPgDumpToWaitAsync(source);
        await stop.CancelAsync();
        var result = await running;

        await Assert.That(result.Outcome).IsEqualTo(BackupOutcome.Cancelled);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("yesterday's backup");
        await Assert.That(File.Exists(path + ".partial")).IsFalse();

        await using var rollback = new NpgsqlCommand("ROLLBACK", blocker);
        await rollback.ExecuteNonQueryAsync();
    }

    private static async Task WaitForPgDumpToWaitAsync(NpgsqlDataSource source)
    {
        for (var i = 0; i < 100; i++)
        {
            await using var command = source.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'pg_dump' AND wait_event_type = 'Lock'");
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("pg_dump never started waiting for the lock.");
    }

    /// <summary>Reports on the reporting thread, so the test sees every report before the run returns.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }
}
