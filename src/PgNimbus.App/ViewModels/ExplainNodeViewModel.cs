using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

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

    /// <summary>The tree for a whole plan, its figures read once by <see cref="PlanMetrics"/>.</summary>
    public static ExplainNodeViewModel ForPlan(ExplainResult result) =>
        new(result.Root, result.Root.TotalCost, PlanMetrics.Compute(result));

    public ExplainNodeViewModel(ExplainNode node, double rootTotalCost, IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> metrics)
    {
        Node = node;
        Children = node.Children.Select(c => new ExplainNodeViewModel(c, rootTotalCost, metrics)).ToList();

        // Exclusive (self) quantities come from PlanMetrics, the definition Query Lab
        // compares by too: time and buffers net of the children (an InitPlan's are
        // charged to the node that reads it, a parallel section's time is divided by
        // the leader and its workers), cost likewise. Rows aren't additive down the
        // tree, so the "rows" metric uses the node's own output row count.
        var figures = metrics[node];
        SelfTimeMs = figures.SelfMs ?? 0;
        SelfCost = figures.SelfCost;
        SelfBuffers = figures.SelfBuffers?.Total ?? 0;
        RowCount = figures.ActualRowsTotal ?? node.PlanRows;

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

    /// <summary>
    /// Whether the node's row is open. The plan opens all the way down; the state
    /// lives here rather than on the row because the tree virtualizes, and a row
    /// scrolled out of view is reused for another node.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;

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

}
