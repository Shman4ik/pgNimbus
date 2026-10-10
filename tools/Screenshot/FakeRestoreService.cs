using PgNimbus.Core.Backup;

namespace PgNimbus.Screenshot;

/// <summary>
/// The restore window's service with no server and no pg_restore behind it: a
/// PostgreSQL 17 server called db.example.com, an archive whose listing is the
/// one below, and a run that does whatever the test or scenario says.
/// </summary>
public sealed class FakeRestoreService : IRestoreService
{
    /// <summary>A listing like pg_restore 18 prints for the fixture shop.</summary>
    public static PgArchiveListing ShopListing { get; } = PgArchiveListing.Parse("""
        ;
        ; Archive created at 2026-10-10 09:32:12
        ;     dbname: shop
        ;     Format: CUSTOM
        ;     Dumped from database version: 17.4
        ;     Dumped by pg_dump version: 18.6
        ;
        6; 2615 16388 SCHEMA - sales app
        225; 1259 16418 TABLE public orders app
        220; 1259 16390 TABLE public customers app
        221; 1259 16391 TABLE public order_items app
        222; 1259 16399 TABLE sales invoices reporting
        3461; 0 16418 TABLE DATA public orders app
        3457; 0 16390 TABLE DATA public customers app
        3458; 0 16391 TABLE DATA public order_items app
        3459; 0 16399 TABLE DATA sales invoices reporting
        """);

    public string DatabaseName => "shop";

    public string ServerLabel => "db.example.com";

    public PgVersion ServerVersion { get; set; } = new(17, 4);

    /// <summary>What inspecting a file finds; the shop archive by default.</summary>
    public RestoreInspection Inspection { get; set; } = new(PgArchiveFormat.Custom, ShopListing, null);

    /// <summary>The roles the server lacks.</summary>
    public IReadOnlyList<string> Missing { get; set; } = [];

    /// <summary>Databases that exist, for the name check.</summary>
    public HashSet<string> Databases { get; } = new(StringComparer.Ordinal) { "shop", "postgres" };

    /// <summary>What a run does; by default it succeeds.</summary>
    public Func<PgToolInstall, RestorePlan, IProgress<RestoreProgress>?, CancellationToken, Task<RestoreResult>> Run { get; set; } =
        (_, plan, _, _) => Task.FromResult(new RestoreResult(
            RestoreOutcome.Succeeded, plan.DatabaseName, TimeSpan.FromSeconds(12), null, null, "pg_restore: creating TABLE \"public.orders\"", false));

    /// <summary>Every plan a run was asked for, oldest first.</summary>
    public List<RestorePlan> Runs { get; } = [];

    public Task<PgVersion> GetServerVersionAsync(CancellationToken cancellationToken) => Task.FromResult(ServerVersion);

    public Task<RestoreInspection> InspectAsync(PgToolInstall tool, string path, CancellationToken cancellationToken) =>
        Task.FromResult(Inspection);

    public Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyList<string> roles, CancellationToken cancellationToken) =>
        Task.FromResult(Missing);

    public Task<string> SuggestDatabaseNameAsync(string? archiveDatabase, CancellationToken cancellationToken) =>
        Task.FromResult(RestoreService.Candidates(archiveDatabase ?? DatabaseName).First(name => !Databases.Contains(name)));

    public Task<bool> DatabaseExistsAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Databases.Contains(name));

    public Task<RestoreResult> RunAsync(PgToolInstall tool, RestorePlan plan, PgArchiveListing listing, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        Runs.Add(plan);
        return Run(tool, plan, progress, cancellationToken);
    }
}
