using System.Diagnostics;
using Npgsql;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Backup;

/// <summary>How a backup ended.</summary>
public enum BackupOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>What a backup run produced.</summary>
/// <param name="Error">pg_dump's first error (or why it couldn't start), for a failed run.</param>
/// <param name="Hint">What to do about <paramref name="Error"/>, when it is a familiar one.</param>
/// <param name="Log">Everything pg_dump wrote to standard error.</param>
public sealed record BackupResult(
    BackupOutcome Outcome,
    string OutputPath,
    long Bytes,
    TimeSpan Elapsed,
    int TablesSaved,
    string? Error,
    string? Hint,
    string Log);

/// <summary>
/// What the backup window needs from <see cref="BackupService"/>; the
/// screenshot scenarios and the UI tests stand in a fake, since they have no
/// server and no pg_dump.
/// </summary>
public interface IBackupService
{
    /// <summary>The database the window is connected to.</summary>
    string DatabaseName { get; }

    /// <summary>The connection pg_dump uses.</summary>
    PgToolConnection Connection { get; }

    Task<PgVersion> GetServerVersionAsync(CancellationToken cancellationToken);

    string Preview(PgToolInstall tool, BackupPlan plan);

    Task<BackupResult> RunAsync(PgToolInstall tool, BackupPlan plan, IProgress<BackupProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Backs up a database, a schema or a table with PostgreSQL's own pg_dump,
/// connected exactly as the window is (<see cref="PgToolConnection"/>). pg_dump
/// rather than anything of the app's own because its output is what every
/// PostgreSQL tool restores, it knows every object type of every version, and
/// a backup that only pgNimbus could read would be a trap.
/// </summary>
public sealed class BackupService(NpgsqlDataSource dataSource, PgToolConnection connection) : IBackupService
{
    /// <summary>How often a running backup reports how much it has written.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public PgToolConnection Connection { get; } = connection;

    /// <summary>The database the window is connected to, which a backup saves.</summary>
    public string DatabaseName => Connection.Database;

    /// <summary>The server's release, which decides the oldest pg_dump that can back it up.</summary>
    public async Task<PgVersion> GetServerVersionAsync(CancellationToken cancellationToken)
    {
        await using var session = await dataSource.OpenConnectionAsync(cancellationToken);
        return PgVersion.FromServer(session.PostgreSqlVersion);
    }

    /// <summary>
    /// The tables whose rows <paramref name="scope"/> saves, with their size on
    /// disk (heap and TOAST, no indexes, which pg_dump doesn't copy), for the
    /// progress bar. Ordinary tables and partitions only: those are what pg_dump
    /// announces as "dumping contents"; a materialized view's rows are rebuilt at
    /// restore, a foreign table's stay remote, and a table an extension owns comes
    /// back with the extension.
    /// <para>
    /// The size is <c>relpages</c>, the planner's count from the last VACUUM or
    /// ANALYZE, not <c>pg_table_size</c>: that one opens the table and so waits
    /// for its lock, and a table held by a long <c>ALTER TABLE</c> would leave the
    /// backup stuck before pg_dump had even started (the stop test found it). An
    /// estimate is all a progress bar needs; a table never analyzed counts as small.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BackupTable>> GetTablesAsync(BackupScope scope, CancellationToken cancellationToken)
    {
        var target = scope.Kind switch
        {
            BackupScopeKind.Schema => "n.nspname = @schema",
            BackupScopeKind.Table when scope.IsPartitioned => "c.oid IN (SELECT oid FROM target)",
            BackupScopeKind.Table => "n.nspname = @schema AND c.relname = @table",
            _ => "TRUE",
        };

        var sql = InternalSql.Tag($"""
            WITH RECURSIVE target AS (
                SELECT c.oid
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table
                UNION ALL
                SELECT i.inhrelid
                FROM pg_catalog.pg_inherits i
                JOIN target t ON i.inhparent = t.oid
            )
            SELECT n.nspname, c.relname,
                   (greatest(c.relpages, 0)::bigint + coalesce(greatest(t.relpages, 0), 0))
                       * pg_catalog.current_setting('block_size')::bigint
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_class t ON t.oid = c.reltoastrelid
            WHERE c.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg\_toast%'
              AND n.nspname NOT LIKE 'pg\_temp\_%'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_depend d
                  WHERE d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    AND d.objid = c.oid AND d.deptype = 'e')
              AND {target}
            """);

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("schema", scope.Schema ?? "");
        command.Parameters.AddWithValue("table", scope.Table ?? "");
        var tables = new List<BackupTable>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new BackupTable(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
        }

        return tables;
    }

    /// <summary>
    /// The pg_dump command for <paramref name="plan"/>. With
    /// <paramref name="forPreview"/> it writes to the chosen file, as someone
    /// copying the command would; the run itself writes to
    /// <see cref="BackupPlan.PartialPath"/> first.
    /// </summary>
    public PgToolInvocation BuildInvocation(
        PgToolInstall tool,
        BackupPlan plan,
        IReadOnlyList<BackupTable> tables,
        string? trustedRootsPath,
        bool forPreview = false)
    {
        var arguments = plan.PgDumpArguments(tool.Version, Connection.ToConnectionString(trustedRootsPath), tables);
        if (forPreview)
        {
            arguments = [.. arguments.Select(a => a == "--file=" + plan.PartialPath ? "--file=" + plan.OutputPath : a)];
        }

        return new PgToolInvocation(tool.PathOf(PgTool.PgDump), arguments, tool.ExtraPath, Connection.Password);
    }

    /// <summary>
    /// The command line shown under the form: what runs, minus the password,
    /// which never is an argument. A verifying connection names the file of
    /// trusted CAs the run writes (<see cref="TrustedRoots"/>).
    /// </summary>
    public string Preview(PgToolInstall tool, BackupPlan plan) =>
        PgToolProcess.CommandLine(BuildInvocation(
            tool,
            plan,
            [],
            Connection.NeedsTrustedRoots ? AppDataPaths.Resolve(TrustedRoots.FileName) : null,
            forPreview: true));

    /// <summary>
    /// Runs the backup. pg_dump writes to <see cref="BackupPlan.PartialPath"/>,
    /// which replaces the chosen file only when pg_dump exits cleanly, so a
    /// failed or stopped backup leaves whatever was there before untouched and
    /// no half-written file behind. Cancelling stops pg_dump (and anything it
    /// started) and reports <see cref="BackupOutcome.Cancelled"/>.
    /// </summary>
    public async Task<BackupResult> RunAsync(
        PgToolInstall tool,
        BackupPlan plan,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var log = new PgToolLog();
        var stopwatch = Stopwatch.StartNew();

        BackupResult Failed(string error, string? hint = null) =>
            new(BackupOutcome.Failed, plan.OutputPath, 0, stopwatch.Elapsed, 0, error, hint, log.Text);

        var directory = Path.GetDirectoryName(Path.GetFullPath(plan.OutputPath));
        if (directory is null || !Directory.Exists(directory))
        {
            return Failed($"The folder {directory} doesn't exist.");
        }

        IReadOnlyList<BackupTable> tables;
        try
        {
            tables = await GetTablesAsync(plan.Scope, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // The bar then counts tables as pg_dump names them, without sizes;
            // the backup itself does not depend on this read.
            tables = [];
        }

        string? trustedRoots = null;
        if (Connection.NeedsTrustedRoots)
        {
            try
            {
                trustedRoots = TrustedRoots.WriteForTools();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // libpq then looks for ~/.postgresql/root.crt, and says so if
                // it is missing; the hint under the error covers that.
            }
        }

        TryDelete(plan.PartialPath);

        var tracker = new PgDumpProgressTracker(tables, plan.Content == BackupContent.StructureOnly);
        var gate = new object();
        void Report()
        {
            BackupProgress snapshot;
            lock (gate)
            {
                snapshot = tracker.Snapshot(FileLength(plan.PartialPath), stopwatch.Elapsed);
            }

            progress?.Report(snapshot);
        }

        using var ticker = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ticking = TickAsync(Report, ticker.Token);

        int exitCode;
        try
        {
            exitCode = await PgToolProcess.RunAsync(
                BuildInvocation(tool, plan, tables, trustedRoots),
                line =>
                {
                    log.Add(line);
                    bool changed;
                    lock (gate)
                    {
                        changed = tracker.Observe(line);
                    }

                    if (changed)
                    {
                        Report();
                    }
                },
                onStandardOutput: null,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryDelete(plan.PartialPath);
            return new BackupResult(BackupOutcome.Cancelled, plan.OutputPath, 0, stopwatch.Elapsed, 0, null, null, log.Text);
        }
        catch (PgToolStartException ex)
        {
            TryDelete(plan.PartialPath);
            return Failed(ex.Message, "Check the folder pgNimbus uses for pg_dump in Settings, on the Data tab.");
        }
        finally
        {
            await ticker.CancelAsync();
            await ticking;
        }

        if (exitCode != 0)
        {
            TryDelete(plan.PartialPath);
            var error = log.Errors.FirstOrDefault() ?? PgToolLocator.DescribeFailure(new PgToolOutput(exitCode, "", log.Text));
            return Failed(error, PgToolErrorHints.For(error, PgTool.PgDump, Connection.IsTunnelled));
        }

        try
        {
            File.Move(plan.PartialPath, plan.OutputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"The backup finished, but it couldn't be saved as {plan.OutputPath}: {ex.Message}. It's still in {plan.PartialPath}.");
        }

        lock (gate)
        {
            tracker.Finish();
        }

        var bytes = FileLength(plan.OutputPath);
        progress?.Report(tracker.Snapshot(bytes, stopwatch.Elapsed));
        return new BackupResult(BackupOutcome.Succeeded, plan.OutputPath, bytes, stopwatch.Elapsed, tracker.TablesDone, null, null, log.Text);
    }

    private static async Task TickAsync(Action report, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                report();
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .partial is harmless and named as one.
        }
    }
}
