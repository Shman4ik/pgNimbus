using Npgsql;
using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// The round trip issue #381 asks for: a real pg_dump backup restored by a real
/// pg_restore, and the rows compared. Plus what a restore must never do: leave
/// a half-restored database behind (it is one transaction; a stop or a failure
/// changes nothing, and a database the restore created is removed again), or
/// run a SQL script (only archives are restored).
/// Gated like <see cref="BackupServiceLiveTests"/> (see <see cref="LiveBackupDatabase"/>).
/// </summary>
[NotInParallel(nameof(LiveBackupDatabase))]
public class RestoreServiceLiveTests
{
    private const string Target = "pgnimbus_restore_test";
    private const string GhostSource = "pgnimbus_restore_ghost_src";
    private const string GhostRole = "pgnimbus_restore_ghost";

    private sealed record Setup(PgToolInstall Tool, NpgsqlDataSource Source, PgToolConnection Connection, string Archive) : IAsyncDisposable
    {
        public RestoreService Restores => new(Source, Connection);

        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    /// <summary>The shop database, backed up whole with the real pg_dump.</summary>
    private static async Task<Setup> BackedUpShopAsync()
    {
        LiveBackupDatabase.SkipIfNoServer();
        await LiveBackupDatabase.CreateAsync(LiveBackupDatabase.Name, LiveBackupDatabase.ShopSeed);
        await LiveBackupDatabase.DropAsync(Target);
        await LiveBackupDatabase.DropAsync(Target + "_restored");
        var connectionString = LiveBackupDatabase.ConnectionStringFor(LiveBackupDatabase.Name);
        var source = NpgsqlDataSource.Create(connectionString);
        var connection = PgToolConnection.FromConnectionString(connectionString);
        var backups = new BackupService(source, connection);
        var tool = await LiveBackupDatabase.RequireToolsAsync(await backups.GetServerVersionAsync(CancellationToken.None));

        var archive = Path.Combine(Path.GetTempPath(), "pgnimbus-restore-tests", Guid.NewGuid().ToString("N"), "shop.dump");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var backup = await backups.RunAsync(tool, new BackupPlan(BackupScope.Database, BackupContent.Everything, archive), null, CancellationToken.None);
        if (backup.Outcome != BackupOutcome.Succeeded)
        {
            throw new InvalidOperationException($"The backup the restore starts from failed: {backup.Error}\n{backup.Log}");
        }

        return new Setup(tool, source, connection, archive);
    }

    private static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(LiveBackupDatabase.ConnectionStringFor(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<bool> DatabaseExistsAsync(string name) =>
        (bool)(await ScalarAsync("postgres", $"SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = '{name}')"))!;

    [Test]
    public async Task A_backup_restored_into_a_new_database_has_every_row_back()
    {
        await using var setup = await BackedUpShopAsync();
        var restores = setup.Restores;

        var inspection = await restores.InspectAsync(setup.Tool, setup.Archive, CancellationToken.None);
        await Assert.That(inspection.CanRestore).IsTrue().Because(inspection.Problem ?? "");
        await Assert.That(inspection.Listing!.DatabaseName).IsEqualTo(LiveBackupDatabase.Name);
        await Assert.That(inspection.Listing.TableDataCount).IsEqualTo(4);

        var reports = new List<RestoreProgress>();
        var result = await restores.RunAsync(
            setup.Tool,
            new RestorePlan(setup.Archive, RestoreTarget.NewDatabase, Target, KeepOwners: true),
            inspection.Listing,
            new SynchronousProgress<RestoreProgress>(reports.Add),
            CancellationToken.None);

        try
        {
            await Assert.That(result.Outcome).IsEqualTo(RestoreOutcome.Succeeded).Because(result.Error ?? result.Log);
            await Assert.That(reports.Any(r => r.Stage == RestoreStage.RestoringData)).IsTrue();
            await Assert.That(reports[^1].Fraction).IsEqualTo(1d);

            await Assert.That(await ScalarAsync(Target, "SELECT count(*) FROM public.customers")).IsEqualTo(2000L);
            await Assert.That(await ScalarAsync(Target, "SELECT count(*) FROM sales.orders")).IsEqualTo(20000L);
            await Assert.That(await ScalarAsync(Target, "SELECT count(*) FROM sales.events")).IsEqualTo(5000L);
            await Assert.That(await ScalarAsync(Target, "SELECT string_agg(v, ',' ORDER BY \"Col \"\"x\"\"\") FROM \"Odd \"\"Schema\"\" *?\".\"Weird.Table\""))
                .IsEqualTo("one,two");
            await Assert.That(await ScalarAsync(Target, "SELECT count(*) FROM sales.order_totals")).IsEqualTo(2000L);
            await Assert.That(await ScalarAsync(Target, "SELECT sum(total) FROM sales.orders"))
                .IsEqualTo(await ScalarAsync(LiveBackupDatabase.Name, "SELECT sum(total) FROM sales.orders"));
            // The identity carries on where the original stopped.
            await Assert.That(await ScalarAsync(Target, "INSERT INTO sales.orders (customer_id, total) VALUES (1, 1) RETURNING id")).IsEqualTo(20001L);
        }
        finally
        {
            await LiveBackupDatabase.DropAsync(Target);
        }
    }

    [Test]
    public async Task Restoring_into_the_current_database_puts_back_what_was_changed()
    {
        await using var setup = await BackedUpShopAsync();
        var inspection = await setup.Restores.InspectAsync(setup.Tool, setup.Archive, CancellationToken.None);

        await ScalarAsync(LiveBackupDatabase.Name, "UPDATE sales.orders SET total = 0; DROP TABLE \"Odd \"\"Schema\"\" *?\".\"Weird.Table\"; CREATE TABLE public.kept (id int)");

        var result = await setup.Restores.RunAsync(
            setup.Tool,
            new RestorePlan(setup.Archive, RestoreTarget.CurrentDatabase, LiveBackupDatabase.Name, KeepOwners: true),
            inspection.Listing!,
            null,
            CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(RestoreOutcome.Succeeded).Because(result.Error ?? result.Log);
        await Assert.That(await ScalarAsync(LiveBackupDatabase.Name, "SELECT count(*) FROM sales.orders WHERE total = 0")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(LiveBackupDatabase.Name, "SELECT count(*) FROM \"Odd \"\"Schema\"\" *?\".\"Weird.Table\"")).IsEqualTo(2L);
        // What the backup doesn't hold stays.
        await Assert.That(await ScalarAsync(LiveBackupDatabase.Name, "SELECT to_regclass('public.kept') IS NOT NULL")).IsEqualTo(true);
    }

    [Test]
    public async Task Stopping_a_restore_into_the_current_database_leaves_it_as_it_was()
    {
        await using var setup = await BackedUpShopAsync();
        var inspection = await setup.Restores.InspectAsync(setup.Tool, setup.Archive, CancellationToken.None);
        await ScalarAsync(LiveBackupDatabase.Name, "UPDATE sales.orders SET total = 0 WHERE id = 1");

        // The restore drops sales.orders first, and a reader holding the table
        // keeps that DROP waiting for as long as the test wants.
        await using var blocker = await setup.Source.OpenConnectionAsync();
        await using (var begin = new NpgsqlCommand("BEGIN; SELECT count(*) FROM sales.orders", blocker))
        {
            await begin.ExecuteNonQueryAsync();
        }

        using var stop = new CancellationTokenSource();
        var running = setup.Restores.RunAsync(
            setup.Tool,
            new RestorePlan(setup.Archive, RestoreTarget.CurrentDatabase, LiveBackupDatabase.Name, KeepOwners: true),
            inspection.Listing!,
            null,
            stop.Token);
        await WaitForAsync(setup.Source, "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'pg_restore' AND wait_event_type = 'Lock'");
        await stop.CancelAsync();
        var result = await running;

        await using (var rollback = new NpgsqlCommand("ROLLBACK", blocker))
        {
            await rollback.ExecuteNonQueryAsync();
        }

        await Assert.That(result.Outcome).IsEqualTo(RestoreOutcome.Cancelled);
        // One transaction: the drops before the stop were rolled back with it.
        await Assert.That(await ScalarAsync(LiveBackupDatabase.Name, "SELECT total FROM sales.orders WHERE id = 1")).IsEqualTo(0m);
        await Assert.That(await ScalarAsync(LiveBackupDatabase.Name, "SELECT count(*) FROM sales.orders")).IsEqualTo(20000L);
    }

    [Test]
    public async Task A_role_this_server_lacks_fails_the_restore_and_removes_the_new_database_unless_owners_are_dropped()
    {
        LiveBackupDatabase.SkipIfNoServer();
        await using var setup = await BackedUpShopAsync();

        // A backup whose table belongs to a role that is then dropped, the way a
        // production role is missing on a laptop.
        await LiveBackupDatabase.DropAsync(GhostSource);
        await ScalarAsync("postgres", $"DROP ROLE IF EXISTS {GhostRole}; CREATE ROLE {GhostRole}");
        await ScalarAsync("postgres", $"CREATE DATABASE {GhostSource}");
        await ScalarAsync(GhostSource, $"CREATE TABLE public.t (id int); INSERT INTO public.t VALUES (1), (2); ALTER TABLE public.t OWNER TO {GhostRole}");
        var ghostString = LiveBackupDatabase.ConnectionStringFor(GhostSource);
        await using var ghostSource = NpgsqlDataSource.Create(ghostString);
        var archive = Path.Combine(Path.GetDirectoryName(setup.Archive)!, "ghost.dump");
        var backup = await new BackupService(ghostSource, PgToolConnection.FromConnectionString(ghostString))
            .RunAsync(setup.Tool, new BackupPlan(BackupScope.Database, BackupContent.Everything, archive), null, CancellationToken.None);
        await Assert.That(backup.Outcome).IsEqualTo(BackupOutcome.Succeeded).Because(backup.Error ?? "");
        await ghostSource.DisposeAsync();
        await LiveBackupDatabase.DropAsync(GhostSource);
        await ScalarAsync("postgres", $"DROP ROLE {GhostRole}");

        var restores = setup.Restores;
        var inspection = await restores.InspectAsync(setup.Tool, archive, CancellationToken.None);
        await Assert.That(inspection.Listing!.Owners).Contains(GhostRole);
        await Assert.That(await restores.MissingRolesAsync(inspection.Listing.Owners, CancellationToken.None)).IsEquivalentTo([GhostRole]);

        var kept = await restores.RunAsync(
            setup.Tool, new RestorePlan(archive, RestoreTarget.NewDatabase, Target, KeepOwners: true), inspection.Listing, null, CancellationToken.None);

        await Assert.That(kept.Outcome).IsEqualTo(RestoreOutcome.Failed);
        await Assert.That(kept.Error).Contains($"role \"{GhostRole}\" does not exist");
        await Assert.That(kept.Hint).Contains("Keep owners and permissions");
        await Assert.That(kept.CreatedDatabaseRemoved).IsTrue();
        await Assert.That(await DatabaseExistsAsync(Target)).IsFalse();

        var dropped = await restores.RunAsync(
            setup.Tool, new RestorePlan(archive, RestoreTarget.NewDatabase, Target, KeepOwners: false), inspection.Listing, null, CancellationToken.None);
        try
        {
            await Assert.That(dropped.Outcome).IsEqualTo(RestoreOutcome.Succeeded).Because(dropped.Error ?? dropped.Log);
            await Assert.That(await ScalarAsync(Target, "SELECT count(*) FROM public.t")).IsEqualTo(2L);
            await Assert.That(await ScalarAsync(Target, "SELECT tableowner = current_user FROM pg_tables WHERE tablename = 't'")).IsEqualTo(true);
        }
        finally
        {
            await LiveBackupDatabase.DropAsync(Target);
        }
    }

    [Test]
    public async Task A_taken_name_gets_restored_on_the_end_and_scripts_are_not_restored()
    {
        await using var setup = await BackedUpShopAsync();
        var restores = setup.Restores;

        await Assert.That(await restores.SuggestDatabaseNameAsync(LiveBackupDatabase.Name, CancellationToken.None))
            .IsEqualTo(LiveBackupDatabase.Name + "_restored");
        await Assert.That(await restores.SuggestDatabaseNameAsync(Target, CancellationToken.None)).IsEqualTo(Target);

        var script = Path.Combine(Path.GetDirectoryName(setup.Archive)!, "shop.sql");
        var backup = await new BackupService(setup.Source, setup.Connection)
            .RunAsync(setup.Tool, new BackupPlan(BackupScope.Database, BackupContent.StructureOnly, script), null, CancellationToken.None);
        await Assert.That(backup.Outcome).IsEqualTo(BackupOutcome.Succeeded);

        var inspection = await restores.InspectAsync(setup.Tool, script, CancellationToken.None);
        await Assert.That(inspection.Format).IsEqualTo(PgArchiveFormat.PlainSql);
        await Assert.That(inspection.CanRestore).IsFalse();
        await Assert.That(inspection.Problem).Contains("psql");
    }

    private static async Task WaitForAsync(NpgsqlDataSource source, string countSql)
    {
        for (var i = 0; i < 150; i++)
        {
            await using var command = source.CreateCommand(countSql);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Never true: {countSql}");
    }

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
