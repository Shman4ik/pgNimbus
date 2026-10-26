using System.Globalization;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.QueryLab;

/// <summary>
/// The BUFFERS counters of a node or a whole plan, in blocks. PostgreSQL reports a
/// node's counters including everything below it.
/// </summary>
public readonly record struct BufferCounts(
    long SharedHit,
    long SharedRead,
    long SharedDirtied,
    long SharedWritten,
    long LocalHit,
    long LocalRead,
    long LocalDirtied,
    long LocalWritten,
    long TempRead,
    long TempWritten)
{
    public long Total =>
        SharedHit + SharedRead + SharedDirtied + SharedWritten
        + LocalHit + LocalRead + LocalDirtied + LocalWritten + TempRead + TempWritten;

    /// <summary>Reads the counters from detail lines; a counter that isn't there is a zero.</summary>
    public static BufferCounts From(IReadOnlyList<KeyValuePair<string, string>> details)
    {
        long Get(string key)
        {
            foreach (var pair in details)
            {
                if (pair.Key == key)
                {
                    return double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                        ? ExplainService.ToLong(value) ?? 0
                        : 0;
                }
            }

            return 0;
        }

        return new BufferCounts(
            Get("Shared Hit Blocks"), Get("Shared Read Blocks"), Get("Shared Dirtied Blocks"), Get("Shared Written Blocks"),
            Get("Local Hit Blocks"), Get("Local Read Blocks"), Get("Local Dirtied Blocks"), Get("Local Written Blocks"),
            Get("Temp Read Blocks"), Get("Temp Written Blocks"));
    }

    public static BufferCounts operator -(BufferCounts a, BufferCounts b) => new(
        Math.Max(0, a.SharedHit - b.SharedHit), Math.Max(0, a.SharedRead - b.SharedRead),
        Math.Max(0, a.SharedDirtied - b.SharedDirtied), Math.Max(0, a.SharedWritten - b.SharedWritten),
        Math.Max(0, a.LocalHit - b.LocalHit), Math.Max(0, a.LocalRead - b.LocalRead),
        Math.Max(0, a.LocalDirtied - b.LocalDirtied), Math.Max(0, a.LocalWritten - b.LocalWritten),
        Math.Max(0, a.TempRead - b.TempRead), Math.Max(0, a.TempWritten - b.TempWritten));

    public static BufferCounts operator +(BufferCounts a, BufferCounts b) => new(
        a.SharedHit + b.SharedHit, a.SharedRead + b.SharedRead, a.SharedDirtied + b.SharedDirtied,
        a.SharedWritten + b.SharedWritten, a.LocalHit + b.LocalHit, a.LocalRead + b.LocalRead,
        a.LocalDirtied + b.LocalDirtied, a.LocalWritten + b.LocalWritten, a.TempRead + b.TempRead,
        a.TempWritten + b.TempWritten);
}

/// <summary>
/// One node's figures, read once so every consumer means the same thing by them.
/// Null means "not measured", never zero: a plain EXPLAIN has no actual rows, an
/// ANALYZE with TIMING OFF has rows but no time, a plan without BUFFERS has no
/// counters.
/// </summary>
public sealed record PlanNodeMetrics
{
    /// <summary>True when the node carries ANALYZE figures (it may still have been never executed).</summary>
    public bool Analyzed { get; init; }

    /// <summary>ANALYZE ran and this node never did (loops = 0).</summary>
    public bool NeverExecuted { get; init; }

    public long? Loops { get; init; }

    /// <summary>The planner's estimate, which is per loop.</summary>
    public double EstimatedRows { get; init; }

    /// <summary>Actual rows per loop, comparable with <see cref="EstimatedRows"/>.</summary>
    public double? ActualRows { get; init; }

    /// <summary>
    /// Actual rows over every loop. Before PostgreSQL 18 the per-loop figure is rounded
    /// to a whole number, so this is approximate when there was more than one loop.
    /// </summary>
    public double? ActualRowsTotal { get; init; }

    /// <summary>Time in this node and below it, in milliseconds; null without ANALYZE timing.</summary>
    public double? InclusiveMs { get; init; }

    /// <summary>Time in this node alone (<see cref="InclusiveMs"/> minus its children's); null without timing.</summary>
    public double? SelfMs { get; init; }

    /// <summary>
    /// True inside a parallel section, where PostgreSQL averages time over the leader
    /// and its workers and the time here is divided back by their number.
    /// </summary>
    public bool TimeApproximate { get; init; }

    /// <summary>The planner's cost, in planner units (not time), including the children's.</summary>
    public double TotalCost { get; init; }

    public double SelfCost { get; init; }

    /// <summary>Counters including the children's; null when the plan doesn't say whether BUFFERS was on.</summary>
    public BufferCounts? Buffers { get; init; }

    /// <summary>Counters of this node alone; null as <see cref="Buffers"/>.</summary>
    public BufferCounts? SelfBuffers { get; init; }

    /// <summary>Why this node went to disk ("sort on disk (2400 kB)"), or null.</summary>
    public string? Spill { get; init; }

    public long? WorkersPlanned { get; init; }

    public long? WorkersLaunched { get; init; }
}

/// <summary>
/// Computes <see cref="PlanNodeMetrics"/> for every node of a plan. Core-pure and
/// deterministic, a sibling of <see cref="PlanAnalyzer"/>; the plan tree's heat bars and
/// a plan comparison both read it, so "self time" has one definition.
/// </summary>
public static class PlanMetrics
{
    /// <summary>The metrics of every node of <paramref name="result"/>, keyed by node instance.</summary>
    public static IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> Compute(ExplainResult result)
    {
        var map = new Dictionary<ExplainNode, PlanNodeMetrics>(ReferenceEqualityComparer.Instance);
        // Node counters exist only under ANALYZE, and a missing one is a zero only when the
        // plan says its nodes were measured.
        var buffersCaptured = result.Root.ActualLoops is not null
            && result.Capture.Buffers != false
            && (result.Capture.Buffers == true || AnyBufferCounter(result.Root));
        Visit(result.Root, divisor: 1, buffersCaptured, HasTiming(result.Root), map);
        return map;
    }

    /// <param name="timed">True when ANALYZE timed the plan, so a never-executed node took no time rather than an unknown one.</param>
    private static PlanNodeMetrics Visit(
        ExplainNode node, double divisor, bool buffersCaptured, bool timed, Dictionary<ExplainNode, PlanNodeMetrics> map)
    {
        // Under a Gather every node runs in the leader and each worker, and EXPLAIN
        // averages its time over all of them; dividing loops back by their number gives
        // a figure comparable with the Gather's own wall-clock time.
        var workersPlanned = ParseLong(Detail(node, "Workers Planned"));
        var workersLaunched = ParseLong(Detail(node, "Workers Launched"));
        var childDivisor = divisor;
        if (PlanNodeLabel.FamilyOf(node.NodeType) == PlanNodeFamily.Gather)
        {
            childDivisor = (workersLaunched ?? workersPlanned ?? 0) + 1;
        }

        var children = new List<(ExplainNode Node, PlanNodeMetrics Metrics)>(node.Children.Count);
        foreach (var child in node.Children)
        {
            children.Add((child, Visit(child, childDivisor, buffersCaptured, timed, map)));
        }

        // An InitPlan (a CTE among them) is evaluated by whichever node first reads its
        // result, and its time and buffers are charged there, so it is not subtracted
        // the way an ordinary child is.
        var subtracted = children.Where(c => c.Node.ParentRelationship != "InitPlan").Select(c => c.Metrics).ToList();

        var loops = node.ActualLoops;
        var analyzed = loops is not null;
        double? inclusive = node.ActualTotalTimeMs is { } perLoop
            ? perLoop * (loops ?? 1) / divisor
            : loops == 0 && timed ? 0 : null;
        double? self = inclusive is { } total
            ? Math.Max(0, total - subtracted.Sum(c => c.InclusiveMs ?? 0))
            : null;

        BufferCounts? buffers = buffersCaptured ? BufferCounts.From(node.Details) : null;
        BufferCounts? selfBuffers = buffers is { } own
            ? subtracted.Aggregate(own, (acc, c) => acc - (c.Buffers ?? default))
            : null;

        var metrics = new PlanNodeMetrics
        {
            Analyzed = analyzed,
            NeverExecuted = loops == 0,
            Loops = loops,
            EstimatedRows = node.PlanRows,
            ActualRows = node.ActualRows,
            ActualRowsTotal = node.ActualRows is { } rows ? rows * (loops ?? 1) : null,
            InclusiveMs = inclusive,
            SelfMs = self,
            TimeApproximate = divisor > 1,
            TotalCost = node.TotalCost,
            SelfCost = Math.Max(0, node.TotalCost - subtracted.Sum(c => c.TotalCost)),
            Buffers = buffers,
            SelfBuffers = selfBuffers,
            Spill = SpillOf(node, selfBuffers),
            WorkersPlanned = workersPlanned,
            WorkersLaunched = workersLaunched,
        };
        map[node] = metrics;
        return metrics;
    }

    private static bool HasTiming(ExplainNode node) => node.ActualTotalTimeMs is not null || node.Children.Any(HasTiming);

    private static bool AnyBufferCounter(ExplainNode node) =>
        node.Details.Any(d => ExplainService.IsBufferCounter(d.Key)) || node.Children.Any(AnyBufferCounter);

    /// <summary>
    /// Why the node went to disk. The temp blocks it wrote itself are the signal that
    /// also catches a sort that spilled in a worker while the leader's line says quicksort.
    /// </summary>
    private static string? SpillOf(ExplainNode node, BufferCounts? selfBuffers)
    {
        if (Detail(node, "Sort Method") is { } method && method.Contains("external", StringComparison.OrdinalIgnoreCase))
        {
            return Detail(node, "Sort Space Used") is { } kb ? $"sort on disk ({kb} kB)" : "sort on disk";
        }

        if (ParseLong(Detail(node, "Hash Batches")) is > 1 and var batches)
        {
            return $"hash in {batches} batches";
        }

        if (ParseLong(Detail(node, "HashAgg Batches")) is > 1 and var aggBatches)
        {
            return $"hash aggregate in {aggBatches} batches";
        }

        if (ParseLong(Detail(node, "Disk Usage")) is > 0 and var disk)
        {
            return $"{disk} kB on disk";
        }

        if (selfBuffers is { TempWritten: > 0 } temp)
        {
            return string.Create(CultureInfo.InvariantCulture, $"wrote {temp.TempWritten:N0} temp blocks");
        }

        return null;
    }

    internal static string? Detail(ExplainNode node, string key)
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
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? ExplainService.ToLong(parsed)
            : null;
}
