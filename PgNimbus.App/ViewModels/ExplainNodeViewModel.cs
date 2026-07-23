using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>Which quantity the plan tree's heat bar is scaled by (pev2-style re-color).</summary>
public enum PlanMetric
{
    /// <summary>Exclusive execution time — the default when ANALYZE timing is present.</summary>
    SelfTime,
    Rows,
    Cost,
    Buffers,
}

/// <summary>
/// Presentation wrapper around a single <see cref="ExplainNode"/>: formats the
/// cost/row/timing figures Postgres reports and derives a bar width so the tree
/// reads as a rough visual profile, not just a wall of numbers. The bar re-scales
/// live by the chosen <see cref="PlanMetric"/> (self time, rows, cost, or buffers)
/// via <see cref="ApplyMetric(PlanMetric)"/>; <see cref="IsBottleneck"/> marks the
/// single hottest node in that metric. Observable so the toggle updates the tree
/// in place without rebuilding it (which would drop expansion state).
/// </summary>
public sealed partial class ExplainNodeViewModel : ObservableObject
{
    private const double MaxBarWidth = 200;

    // Buffer-usage counters are cumulative (like actual time), so exclusive
    // self-buffers subtract the children's — and they're prefixed by their pool.
    private static readonly string[] BufferPrefixes = ["Shared ", "Local ", "Temp "];

    public ExplainNodeViewModel(ExplainNode node, double rootTotalCost)
    {
        Node = node;
        Children = node.Children.Select(c => new ExplainNodeViewModel(c, rootTotalCost)).ToList();

        // Exclusive (self) quantities: this node's total minus the same for its
        // children, clamped at 0 for the odd rounding case. Time and buffers are
        // cumulative in the plan JSON, so both need the subtraction; cost likewise
        // (Total Cost includes children). Rows aren't additive down the tree, so
        // the "rows" metric uses the node's own output row count.
        var inclusiveMs = InclusiveMs(node);
        SelfTimeMs = node.ActualTotalTimeMs is null ? 0 : Math.Max(0, inclusiveMs - node.Children.Sum(InclusiveMs));
        SelfCost = Math.Max(0, node.TotalCost - node.Children.Sum(c => c.TotalCost));
        SelfBuffers = Math.Max(0, BufferBlocks(node) - node.Children.Sum(BufferBlocks));
        RowCount = node.ActualRows is { } actual ? actual * (node.ActualLoops ?? 1) : node.PlanRows;

        // A sensible starting bar before the first ApplyMetric (cost profile).
        BarWidth = rootTotalCost > 0 ? Math.Clamp(node.TotalCost / rootTotalCost, 0, 1) * MaxBarWidth : 0;
    }

    public ExplainNode Node { get; }

    public IReadOnlyList<ExplainNodeViewModel> Children { get; }

    [ObservableProperty]
    private double _barWidth;

    /// <summary>The hottest node in the currently-selected metric — tinted as the plan's bottleneck.</summary>
    [ObservableProperty]
    private bool _isBottleneck;

    /// <summary>Exclusive (self) execution time — 0 when the plan carries no ANALYZE timing.</summary>
    public double SelfTimeMs { get; }

    /// <summary>Exclusive (self) planner cost — <c>Total Cost</c> minus the children's.</summary>
    public double SelfCost { get; }

    /// <summary>Rows this node produced (actual × loops when analyzed, else the estimate).</summary>
    public double RowCount { get; }

    /// <summary>Exclusive (self) buffer blocks touched — 0 when the plan carries no BUFFERS data.</summary>
    public double SelfBuffers { get; }

    public bool HasTiming => Node.ActualTotalTimeMs is not null;

    /// <summary>True when this node or any descendant reports buffer usage — gates the "Buffers" toggle.</summary>
    public bool HasAnyBuffers => SelfBuffers > 0 || Children.Any(c => c.HasAnyBuffers);

    public string Title => ExplainTextFormatter.HeaderFor(Node);

    public string CostLabel => $"cost={Node.StartupCost:F2}..{Node.TotalCost:F2} rows={Node.PlanRows} width={Node.PlanWidth}";

    public string? ActualLabel => Node.ActualTotalTimeMs is { } total
        ? $"actual time={Node.ActualStartupTimeMs:F3}..{total:F3} ms rows={Node.ActualRows:0.##} loops={Node.ActualLoops}"
            + (SelfTimeMs > 0 ? $"   self={SelfTimeMs:F3} ms" : null)
        : null;

    /// <summary>
    /// Re-scales the whole subtree's bars by <paramref name="metric"/> and re-marks the
    /// hottest node. Called on the root; when "self time" is asked for but the plan has
    /// no timing (plain EXPLAIN) it falls back to cost, matching the old heat behavior.
    /// </summary>
    public void ApplyMetric(PlanMetric metric)
    {
        var effective = metric == PlanMetric.SelfTime && MaxOf(PlanMetric.SelfTime) <= 0
            ? PlanMetric.Cost
            : metric;

        ApplyMetric(effective, MaxOf(effective));
    }

    private void ApplyMetric(PlanMetric metric, double max)
    {
        var value = ValueFor(metric);
        BarWidth = max > 0 ? Math.Clamp(value / max, 0, 1) * MaxBarWidth : 0;
        IsBottleneck = max > 0 && value >= max;

        foreach (var child in Children)
        {
            child.ApplyMetric(metric, max);
        }
    }

    private double ValueFor(PlanMetric metric) => metric switch
    {
        PlanMetric.SelfTime => SelfTimeMs,
        PlanMetric.Rows => RowCount,
        PlanMetric.Cost => SelfCost,
        PlanMetric.Buffers => SelfBuffers,
        _ => SelfTimeMs,
    };

    private double MaxOf(PlanMetric metric)
    {
        var max = ValueFor(metric);
        foreach (var child in Children)
        {
            max = Math.Max(max, child.MaxOf(metric));
        }

        return max;
    }

    private static double InclusiveMs(ExplainNode node) =>
        node.ActualTotalTimeMs is { } total ? total * (node.ActualLoops ?? 1) : 0;

    /// <summary>Sum of the node's buffer-usage counters (shared/local/temp hit/read/dirtied/written blocks).</summary>
    private static double BufferBlocks(ExplainNode node)
    {
        double sum = 0;
        foreach (var (key, value) in node.Details)
        {
            if (key.EndsWith(" Blocks", StringComparison.Ordinal)
                && Array.Exists(BufferPrefixes, p => key.StartsWith(p, StringComparison.Ordinal))
                && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var blocks))
            {
                sum += blocks;
            }
        }

        return sum;
    }
}
