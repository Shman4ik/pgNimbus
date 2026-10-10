using PgNimbus.Core.Backup;

namespace PgNimbus.Screenshot;

/// <summary>
/// The backup window's service with no server and no pg_dump behind it: a
/// PostgreSQL 17 server, a command preview built by the real code from a fixed
/// connection, and a run that does whatever the test or scenario says.
/// </summary>
public sealed class FakeBackupService : IBackupService
{
    private readonly BackupService _previews = new(Fixtures.DataSource, Connection);

    public static PgToolConnection Connection { get; } =
        new("db.example.com", 5432, "shop", "app", "never shown", PgNimbus.Core.Connections.SslMode.Require, null);

    PgToolConnection IBackupService.Connection => Connection;

    public string DatabaseName => "shop";

    public PgVersion ServerVersion { get; set; } = new(17, 4);

    /// <summary>What a run does; by default it succeeds at once with a 48 MB file.</summary>
    public Func<PgToolInstall, BackupPlan, IProgress<BackupProgress>?, CancellationToken, Task<BackupResult>> Run { get; set; } =
        (_, plan, _, _) => Task.FromResult(new BackupResult(
            BackupOutcome.Succeeded, plan.OutputPath, 50_593_792, TimeSpan.FromSeconds(42), 41, null, null, "pg_dump: reading schemas"));

    /// <summary>Every plan a run was asked for, oldest first.</summary>
    public List<BackupPlan> Runs { get; } = [];

    public Task<PgVersion> GetServerVersionAsync(CancellationToken cancellationToken) => Task.FromResult(ServerVersion);

    public string Preview(PgToolInstall tool, BackupPlan plan) => _previews.Preview(tool, plan);

    public Task<BackupResult> RunAsync(PgToolInstall tool, BackupPlan plan, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        Runs.Add(plan);
        return Run(tool, plan, progress, cancellationToken);
    }
}
