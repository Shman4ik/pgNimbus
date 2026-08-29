using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>A largest-relations row shaped for the grid: pre-formatted sizes and a glyph.</summary>
public sealed record RelationSizeRow(RelationSize Relation)
{
    public string Schema => Relation.Schema;

    public string Name => Relation.Name;

    public string Kind => Relation.Kind switch
    {
        RelationKind.MaterializedView => "matview",
        RelationKind.PartitionedTable => "partitioned",
        _ => "table",
    };

    public string Total => ByteSize.Format(Relation.TotalBytes);

    public string TableSize => ByteSize.Format(Relation.TableBytes);

    public string IndexSize => ByteSize.Format(Relation.IndexBytes);

    public string Rows => Relation.RowEstimate.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>A scan-usage row: raw counters plus a formatted index-scan percentage and a low-ratio flag.</summary>
public sealed record TableScanRow(TableScanUsage Usage)
{
    public string Schema => Usage.Schema;

    public string Name => Usage.Name;

    public string SeqScan => Usage.SeqScan.ToString("N0", CultureInfo.InvariantCulture);

    public string IdxScan => Usage.IdxScan.ToString("N0", CultureInfo.InvariantCulture);

    public string LiveRows => Usage.LiveTuples.ToString("N0", CultureInfo.InvariantCulture);

    public string DeadRows => Usage.DeadTuples.ToString("N0", CultureInfo.InvariantCulture);

    public string IndexScanPercent =>
        Usage.IndexScanRatio is { } r ? r.ToString("P0", CultureInfo.InvariantCulture) : "—";

    /// <summary>
    /// A table big enough to matter (10k+ live rows) that the planner reaches
    /// mostly via sequential scans — the missing-index smell the panel highlights.
    /// </summary>
    public bool IsSeqScanHeavy =>
        Usage.LiveTuples >= 10_000 && Usage.IndexScanRatio is < 0.5;
}

/// <summary>An unused-index row with its size pre-formatted.</summary>
public sealed record UnusedIndexRow(UnusedIndex Index)
{
    public string Schema => Index.Schema;

    public string Table => Index.Table;

    public string Name => Index.Index;

    public string Size => ByteSize.Format(Index.IndexBytes);
}

/// <summary>
/// Drives the Database Overview window: a one-shot snapshot of database size,
/// cache-hit ratios, the largest relations, per-table scan usage, and unused
/// indexes. Everything is read-only, so unlike the activity view there's no
/// auto-refresh timer — the user hits Refresh to re-snapshot.
/// </summary>
public sealed partial class DatabaseOverviewViewModel(DatabaseStatsService service) : ObservableObject
{
    private const int LargestRelationsLimit = 50;

    private readonly DatabaseStatsService _service = service;

    [ObservableProperty]
    private string _databaseName = "";

    [ObservableProperty]
    private string _databaseSizeText = "";

    [ObservableProperty]
    private string _tableCacheHitText = "—";

    [ObservableProperty]
    private string _indexCacheHitText = "—";

    [ObservableProperty]
    private string _status = "";

    public ObservableCollection<RelationSizeRow> LargestRelations { get; } = [];

    public ObservableCollection<TableScanRow> TableScans { get; } = [];

    public ObservableCollection<UnusedIndexRow> UnusedIndexes { get; } = [];

    // AllowConcurrentExecutions = false disables the Refresh button while a
    // snapshot is in flight, so repeated clicks can't race the ObservableCollection
    // mutations below. The four reads are independent, so they fan out in parallel
    // rather than serially — one round-trip's worth of latency instead of four,
    // which matters over an SSH tunnel. The CancellationToken lets an in-flight
    // snapshot be abandoned (e.g. a re-invoke) instead of running to completion.
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var overviewTask = _service.GetOverviewAsync(ct);
            var largestTask = _service.GetLargestRelationsAsync(LargestRelationsLimit, ct);
            var scansTask = _service.GetTableScanUsageAsync(ct);
            var unusedTask = _service.GetUnusedIndexesAsync(ct);
            await Task.WhenAll(overviewTask, largestTask, scansTask, unusedTask);

            var overview = await overviewTask;
            DatabaseName = overview.DatabaseName;
            DatabaseSizeText = ByteSize.Format(overview.SizeBytes);
            TableCacheHitText = FormatRatio(overview.TableCacheHitRatio);
            IndexCacheHitText = FormatRatio(overview.IndexCacheHitRatio);

            var largest = await largestTask;
            LargestRelations.Clear();
            foreach (var relation in largest)
            {
                LargestRelations.Add(new RelationSizeRow(relation));
            }

            var scans = await scansTask;
            TableScans.Clear();
            foreach (var scan in scans)
            {
                TableScans.Add(new TableScanRow(scan));
            }

            var unused = await unusedTask;
            UnusedIndexes.Clear();
            foreach (var index in unused)
            {
                UnusedIndexes.Add(new UnusedIndexRow(index));
            }

            var unusedBytes = unused.Sum(i => i.IndexBytes);
            Status = $"{LargestRelations.Count} relation{Plural(LargestRelations.Count)} · "
                + $"{UnusedIndexes.Count} unused index{(UnusedIndexes.Count == 1 ? "" : "es")}"
                + (unusedBytes > 0 ? $" wasting {ByteSize.Format(unusedBytes)}" : "")
                + $" · {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // A superseded/abandoned snapshot — not an error worth surfacing.
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static string FormatRatio(double? ratio) =>
        ratio is { } r ? r.ToString("P1", CultureInfo.InvariantCulture) : "—";
}
