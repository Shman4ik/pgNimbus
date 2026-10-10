using System.Diagnostics;
using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Backup;

/// <summary>What a file chosen for a restore turned out to be.</summary>
/// <param name="Listing">What the archive holds, when it is one pg_restore can read.</param>
/// <param name="Problem">Why it can't be restored, in words for the window, or null.</param>
public sealed record RestoreInspection(PgArchiveFormat Format, PgArchiveListing? Listing, string? Problem)
{
    public bool CanRestore => Listing is not null && Problem is null;
}

/// <summary>How a restore ended.</summary>
public enum RestoreOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>What a restore run did.</summary>
/// <param name="CreatedDatabaseRemoved">
/// True when the restore had created a new database and removed it again
/// because the restore failed or was stopped.
/// </param>
public sealed record RestoreResult(
    RestoreOutcome Outcome,
    string Database,
    TimeSpan Elapsed,
    string? Error,
    string? Hint,
    string Log,
    bool CreatedDatabaseRemoved);

/// <summary>What the restore window needs; a fake stands in for it in the screenshots and UI tests.</summary>
public interface IRestoreService
{
    /// <summary>The database the window is connected to.</summary>
    string DatabaseName { get; }

    /// <summary>Where the server is, for the confirmation before replacing the current database.</summary>
    string ServerLabel { get; }

    Task<PgVersion> GetServerVersionAsync(CancellationToken cancellationToken);

    Task<RestoreInspection> InspectAsync(PgToolInstall tool, string path, CancellationToken cancellationToken);

    /// <summary>The roles among <paramref name="roles"/> this server doesn't have.</summary>
    Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyList<string> roles, CancellationToken cancellationToken);

    /// <summary>A name for a new database that doesn't exist yet: the archive's own, or that with <c>_restored</c>.</summary>
    Task<string> SuggestDatabaseNameAsync(string? archiveDatabase, CancellationToken cancellationToken);

    Task<bool> DatabaseExistsAsync(string name, CancellationToken cancellationToken);

    Task<RestoreResult> RunAsync(PgToolInstall tool, RestorePlan plan, PgArchiveListing listing, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Restores a pg_dump archive with PostgreSQL's own pg_restore, into a new
/// database (the default: nothing existing is touched) or into the window's
/// own. Only archives: a plain SQL script would need psql, which also runs the
/// shell commands (<c>\!</c>) a script can hold, and that is how restoring a
/// plain dump became remote code execution in pgAdmin twice (CVE-2025-12762
/// and its bypass, CVE-2025-13780). pg_restore connects with a connection
/// string and runs SQL only.
/// </summary>
public sealed class RestoreService(NpgsqlDataSource dataSource, PgToolConnection connection) : IRestoreService
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(120);

    public PgToolConnection Connection { get; } = connection;

    public string DatabaseName => Connection.Database;

    public string ServerLabel => Connection.IsTunnelled ? $"{Connection.Host} (through SSH)" : Connection.Host;

    public async Task<PgVersion> GetServerVersionAsync(CancellationToken cancellationToken)
    {
        await using var session = await dataSource.OpenConnectionAsync(cancellationToken);
        return PgVersion.FromServer(session.PostgreSqlVersion);
    }

    /// <summary>
    /// What <paramref name="path"/> is, and for an archive what it holds
    /// (<c>pg_restore --list</c>, which reads only its table of contents).
    /// </summary>
    public async Task<RestoreInspection> InspectAsync(PgToolInstall tool, string path, CancellationToken cancellationToken)
    {
        var format = PgArchive.Detect(path);
        switch (format)
        {
            case PgArchiveFormat.Missing:
                return new RestoreInspection(format, null, "The file isn't there any more.");
            case PgArchiveFormat.PlainSql:
                return new RestoreInspection(format, null,
                    "This is a SQL script, pg_dump's plain format. pgNimbus restores pg_dump's archives (.dump). "
                    + "Restore a script with psql (psql -f file.sql); pgNimbus doesn't run psql for you, because psql also runs the "
                    + "shell commands a script can hold.");
            case PgArchiveFormat.Unknown:
                return new RestoreInspection(format, null, "This isn't a pg_dump archive. pgNimbus restores the .dump files pg_dump writes.");
        }

        var output = await PgToolProcess.CaptureAsync(
            new PgToolInvocation(tool.PathOf(PgTool.PgRestore), ["--list", path], tool.ExtraPath),
            ListTimeout,
            cancellationToken);
        if (output.ExitCode != 0)
        {
            var error = ErrorOf(output.StandardError) ?? PgToolLocator.DescribeFailure(output);
            var problem = error.Contains("unsupported version", StringComparison.OrdinalIgnoreCase)
                ? $"This backup was made by a newer pg_dump than this pg_restore ({tool.Version}) can read. Install a newer PostgreSQL client."
                : $"pg_restore can't read this file: {error}";
            return new RestoreInspection(format, null, problem);
        }

        return new RestoreInspection(format, PgArchiveListing.Parse(output.StandardOutput), null);
    }

    private static string? ErrorOf(string standardError)
    {
        var log = new PgToolLog();
        foreach (var line in standardError.Split('\n'))
        {
            log.Add(line.TrimEnd('\r'));
        }

        return log.Errors.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        if (roles.Count == 0)
        {
            return [];
        }

        await using var command = dataSource.CreateCommand(InternalSql.Tag(
            "SELECT rolname FROM pg_catalog.pg_roles WHERE rolname = ANY(@names)"));
        command.Parameters.AddWithValue("names", roles.ToArray());
        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            existing.Add(reader.GetString(0));
        }

        return [.. roles.Where(r => !existing.Contains(r))];
    }

    public async Task<bool> DatabaseExistsAsync(string name, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(InternalSql.Tag(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = @name)"));
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<string> SuggestDatabaseNameAsync(string? archiveDatabase, CancellationToken cancellationToken)
    {
        var stem = string.IsNullOrWhiteSpace(archiveDatabase) ? DatabaseName : archiveDatabase.Trim();
        foreach (var candidate in Candidates(stem))
        {
            if (!await DatabaseExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return Fit(stem, "_" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>The names tried for a new database: the stem, then <c>_restored</c>, <c>_restored_2</c> … each cut to fit.</summary>
    public static IEnumerable<string> Candidates(string stem)
    {
        yield return Fit(stem, "");
        yield return Fit(stem, "_restored");
        for (var i = 2; i <= 20; i++)
        {
            yield return Fit(stem, $"_restored_{i}");
        }
    }

    private static string Fit(string stem, string suffix)
    {
        var name = stem + suffix;
        while (System.Text.Encoding.UTF8.GetByteCount(name) > RestorePlan.MaxDatabaseNameBytes && stem.Length > 1)
        {
            stem = stem[..^1];
            name = stem + suffix;
        }

        return name;
    }

    /// <summary>
    /// Runs the restore. Into a new database it is created first
    /// (<c>TEMPLATE template0</c>, the clean template pg_dump's documentation
    /// asks for, so nothing added to <c>template1</c> collides with what the
    /// backup creates), and removed again if the restore fails or is stopped:
    /// the restore is one transaction, so what is left would be empty anyway.
    /// </summary>
    public async Task<RestoreResult> RunAsync(
        PgToolInstall tool,
        RestorePlan plan,
        PgArchiveListing listing,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var log = new PgToolLog();
        var stopwatch = Stopwatch.StartNew();
        var created = false;

        async Task<RestoreResult> EndAsync(RestoreOutcome outcome, string? error, string? hint)
        {
            var removed = false;
            if (created && outcome != RestoreOutcome.Succeeded)
            {
                removed = await TryDropDatabaseAsync(plan.DatabaseName);
            }

            return new RestoreResult(outcome, plan.DatabaseName, stopwatch.Elapsed, error, hint, log.Text, removed);
        }

        if (plan.Target == RestoreTarget.NewDatabase)
        {
            if (RestorePlan.ValidateDatabaseName(plan.DatabaseName) is { } invalid)
            {
                return await EndAsync(RestoreOutcome.Failed, invalid, null);
            }

            try
            {
                await using var create = dataSource.CreateCommand(
                    $"CREATE DATABASE {SqlIdentifier.Quote(plan.DatabaseName)} TEMPLATE template0");
                await create.ExecuteNonQueryAsync(cancellationToken);
                created = true;
            }
            catch (PostgresException ex)
            {
                var hint = ex.SqlState switch
                {
                    PostgresErrorCodes.InsufficientPrivilege =>
                        "Your role can't create databases. Restore into this database instead, or use a role with CREATEDB.",
                    PostgresErrorCodes.DuplicateDatabase => "Pick another name.",
                    _ => null,
                };
                return await EndAsync(RestoreOutcome.Failed, $"Couldn't create database {plan.DatabaseName}: {ex.MessageText}", hint);
            }
            catch (OperationCanceledException)
            {
                return await EndAsync(RestoreOutcome.Cancelled, null, null);
            }
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
                // libpq then reports the missing root certificate itself.
            }
        }

        var target = Connection.ForDatabase(plan.DatabaseName);
        var tracker = new PgRestoreProgressTracker(listing, clean: plan.Target == RestoreTarget.CurrentDatabase);
        var gate = new object();
        var invocation = new PgToolInvocation(
            tool.PathOf(PgTool.PgRestore),
            plan.PgRestoreArguments(target.ToConnectionString(trustedRoots)),
            tool.ExtraPath,
            Connection.Password);

        int exitCode;
        try
        {
            exitCode = await PgToolProcess.RunAsync(
                invocation,
                line =>
                {
                    log.Add(line);
                    RestoreProgress? snapshot = null;
                    lock (gate)
                    {
                        if (tracker.Observe(line))
                        {
                            snapshot = tracker.Snapshot(stopwatch.Elapsed);
                        }
                    }

                    if (snapshot is not null)
                    {
                        progress?.Report(snapshot);
                    }
                },
                onStandardOutput: null,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await EndAsync(RestoreOutcome.Cancelled, null, null);
        }
        catch (PgToolStartException ex)
        {
            return await EndAsync(RestoreOutcome.Failed, ex.Message, "Check the folder pgNimbus uses for pg_restore in Settings, on the Data tab.");
        }

        if (exitCode != 0)
        {
            var error = log.Errors.FirstOrDefault() ?? PgToolLocator.DescribeFailure(new PgToolOutput(exitCode, "", log.Text));
            return await EndAsync(RestoreOutcome.Failed, error, PgToolErrorHints.For(error, PgTool.PgRestore, Connection.IsTunnelled));
        }

        lock (gate)
        {
            tracker.Finish();
        }

        progress?.Report(tracker.Snapshot(stopwatch.Elapsed));
        return new RestoreResult(RestoreOutcome.Succeeded, plan.DatabaseName, stopwatch.Elapsed, null, null, log.Text, false);
    }

    /// <summary>
    /// Removes a database this restore created. pg_restore's own session may
    /// outlive the program for a moment after a stop, so the database's other
    /// sessions are ended first (they can only be that one: nobody else knew it
    /// existed). False when it couldn't be removed; the window then says so.
    /// </summary>
    private async Task<bool> TryDropDatabaseAsync(string name)
    {
        try
        {
            await using (var terminate = dataSource.CreateCommand(InternalSql.Tag(
                "SELECT pg_catalog.pg_terminate_backend(pid) FROM pg_catalog.pg_stat_activity WHERE datname = @name AND pid <> pg_catalog.pg_backend_pid()")))
            {
                terminate.Parameters.AddWithValue("name", name);
                await terminate.ExecuteNonQueryAsync();
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var drop = dataSource.CreateCommand($"DROP DATABASE IF EXISTS {SqlIdentifier.Quote(name)}");
                    await drop.ExecuteNonQueryAsync();
                    return true;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 10)
                {
                    // A terminated backend takes a moment to go.
                    await Task.Delay(200);
                }
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            return false;
        }
    }
}
