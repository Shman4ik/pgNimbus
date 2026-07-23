using System.Globalization;

namespace PgNimbus.Core.Query;

public enum PlanWarningSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// A single named problem found in a query plan — the thing pgAdmin/DBeaver
/// leave the user to spot for themselves. <see cref="Detail"/> is an actionable
/// one-liner (e.g. "raising work_mem may keep this sort in memory").
/// </summary>
public sealed record PlanWarning(
    PlanWarningSeverity Severity,
    string Title,
    string Detail,
    string NodeType,
    string? Relation);

/// <summary>
/// Walks a parsed <see cref="ExplainResult"/> and reports well-known plan
/// problems (bad row estimates, disk spills, wasteful sequential scans, lossy
/// bitmap heap blocks). Pure and deterministic — no DB round-trip, no clock —
/// so it unit-tests against captured JSON exactly like <see cref="ExplainService.Parse"/>,
/// and a read-only sibling of <c>BlockingTree</c>/<c>JsonTree</c> per hard rule 1.
/// </summary>
public static class PlanAnalyzer
{
    // Deliberately conservative so the warnings strip stays high-signal, not noise.
    private const double EstimateFactorThreshold = 10;   // estimated vs actual rows off by this much
    private const double EstimateCriticalFactor = 100;
    private const double EstimateMinRows = 100;           // ignore misestimates on tiny counts
    private const double SeqScanMinScanned = 1000;        // ignore small seq scans
    private const double SeqScanRemovedFraction = 0.9;    // "filters most rows" = discards ≥ 90%

    public static IReadOnlyList<PlanWarning> Analyze(ExplainResult result)
    {
        var warnings = new List<PlanWarning>();
        Walk(result.Root, warnings);
        return warnings;
    }

    private static void Walk(ExplainNode node, List<PlanWarning> acc)
    {
        InspectRowEstimate(node, acc);
        InspectDiskSpills(node, acc);
        InspectSeqScanFilter(node, acc);
        InspectLossyBitmap(node, acc);

        foreach (var child in node.Children)
        {
            Walk(child, acc);
        }
    }

    /// <summary>Estimated vs actual rows (both per-loop) diverging by ≥ 10× — the classic driver of bad join choices.</summary>
    private static void InspectRowEstimate(ExplainNode node, List<PlanWarning> acc)
    {
        // Needs ANALYZE; a never-executed branch (loops = 0) has no meaningful actual count.
        if (node.ActualRows is not { } actual || node.ActualLoops is not { } loops || loops == 0)
        {
            return;
        }

        double estimated = node.PlanRows;
        var hi = Math.Max(estimated, actual);
        var lo = Math.Min(estimated, actual);
        if (hi < EstimateMinRows)
        {
            return;
        }

        var factor = hi / Math.Max(lo, 1);
        if (factor < EstimateFactorThreshold)
        {
            return;
        }

        var direction = estimated > actual ? "over" : "under";
        var severity = factor >= EstimateCriticalFactor ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning;
        acc.Add(new PlanWarning(
            severity,
            $"Row estimate off by {factor:0}×",
            $"Planner {direction}estimated {ExplainTextFormatter.HeaderFor(node)}: estimated {estimated:0}, actual {actual:0} rows. "
                + "Stale statistics or correlated columns can mislead the planner — try ANALYZE or extended statistics.",
            node.NodeType,
            node.RelationName));
    }

    /// <summary>Sorts / hash joins that spilled to disk — usually a work_mem shortfall.</summary>
    private static void InspectDiskSpills(ExplainNode node, List<PlanWarning> acc)
    {
        if (Detail(node, "Sort Method") is { } method && method.Contains("external", StringComparison.OrdinalIgnoreCase))
        {
            var used = Detail(node, "Sort Space Used") is { } kb ? $" ({kb} kB)" : string.Empty;
            acc.Add(new PlanWarning(
                PlanWarningSeverity.Warning,
                "Sort spilled to disk",
                $"This sort used an on-disk method ({method}){used}. Raising work_mem may keep it in memory.",
                node.NodeType,
                node.RelationName));
        }

        if (ParseLong(Detail(node, "Hash Batches")) is { } batches && batches > 1)
        {
            acc.Add(new PlanWarning(
                PlanWarningSeverity.Warning,
                "Hash spilled to disk",
                $"The hash used {batches} batches (>1 means it didn't fit in memory). Raising work_mem may reduce batching.",
                node.NodeType,
                node.RelationName));
        }
    }

    /// <summary>A sequential scan that reads a lot and throws most of it away with a filter — index candidate.</summary>
    private static void InspectSeqScanFilter(ExplainNode node, List<PlanWarning> acc)
    {
        if (node.NodeType != "Seq Scan"
            || node.ActualRows is not { } returned
            || node.ActualLoops is not { } loops || loops == 0
            || ParseDouble(Detail(node, "Rows Removed by Filter")) is not { } removed)
        {
            return;
        }

        var scanned = returned + removed;
        if (scanned < SeqScanMinScanned || removed < scanned * SeqScanRemovedFraction)
        {
            return;
        }

        var relation = node.RelationName is { } r ? $" on {r}" : string.Empty;
        acc.Add(new PlanWarning(
            PlanWarningSeverity.Warning,
            "Sequential scan discards most rows",
            $"The scan{relation} read {scanned:0} rows and filtered out {removed:0} of them. "
                + "An index on the filtered column(s) would let the planner skip the discarded rows.",
            node.NodeType,
            node.RelationName));
    }

    /// <summary>Bitmap heap scan whose bitmap didn't fit in work_mem, so whole pages were rechecked lossily.</summary>
    private static void InspectLossyBitmap(ExplainNode node, List<PlanWarning> acc)
    {
        if (ParseLong(Detail(node, "Lossy Heap Blocks")) is { } lossy && lossy > 0)
        {
            acc.Add(new PlanWarning(
                PlanWarningSeverity.Warning,
                "Bitmap scan went lossy",
                $"{lossy} heap blocks were rechecked lossily because the bitmap outgrew work_mem. "
                    + "Raising work_mem lets more of the bitmap stay exact.",
                node.NodeType,
                node.RelationName));
        }
    }

    private static string? Detail(ExplainNode node, string key)
    {
        foreach (var pair in node.Details)
        {
            if (pair.Key == key)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
