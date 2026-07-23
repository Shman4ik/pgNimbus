using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Presentation wrapper around a single <see cref="ExplainNode"/>: formats the
/// cost/row/timing figures Postgres reports and derives a bar width so the tree
/// reads as a rough visual profile, not just a wall of numbers. With ANALYZE
/// timing present the bar tracks each node's <see cref="SelfTimeMs"/> (exclusive
/// time) — the quantity that actually points at the bottleneck — falling back to
/// the cost estimate otherwise.
/// </summary>
public sealed class ExplainNodeViewModel
{
    private const double MaxBarWidth = 200;

    public ExplainNodeViewModel(ExplainNode node, double rootTotalCost)
    {
        Node = node;
        Children = node.Children.Select(c => new ExplainNodeViewModel(c, rootTotalCost)).ToList();
        BarWidth = rootTotalCost > 0 ? Math.Clamp(node.TotalCost / rootTotalCost, 0, 1) * MaxBarWidth : 0;

        // Exclusive time: this node's total wall time (per-loop × loops) minus the
        // same for its children. Clamped at 0 for the odd rounding case.
        var inclusive = InclusiveMs(node);
        var childrenInclusive = node.Children.Sum(InclusiveMs);
        SelfTimeMs = node.ActualTotalTimeMs is null ? 0 : Math.Max(0, inclusive - childrenInclusive);
    }

    public ExplainNode Node { get; }

    public IReadOnlyList<ExplainNodeViewModel> Children { get; }

    public double BarWidth { get; private set; }

    /// <summary>Exclusive (self) execution time — 0 when the plan carries no ANALYZE timing.</summary>
    public double SelfTimeMs { get; }

    public bool HasTiming => Node.ActualTotalTimeMs is not null;

    /// <summary>The single slowest node by self time — tinted as the plan's bottleneck.</summary>
    public bool IsBottleneck { get; private set; }

    public string Title => ExplainTextFormatter.HeaderFor(Node);

    public string CostLabel => $"cost={Node.StartupCost:F2}..{Node.TotalCost:F2} rows={Node.PlanRows} width={Node.PlanWidth}";

    public string? ActualLabel => Node.ActualTotalTimeMs is { } total
        ? $"actual time={Node.ActualStartupTimeMs:F3}..{total:F3} ms rows={Node.ActualRows:0.##} loops={Node.ActualLoops}"
            + (SelfTimeMs > 0 ? $"   self={SelfTimeMs:F3} ms" : null)
        : null;

    /// <summary>
    /// Re-bases the bar on self time and marks the slowest node. Called once on the
    /// root after the tree is built; no-op when the plan has no timing (plain EXPLAIN).
    /// </summary>
    public void ApplyTimeHeat()
    {
        var maxSelf = MaxSelfTime();
        if (maxSelf <= 0)
        {
            return;
        }

        ApplyTimeHeat(maxSelf);
    }

    private void ApplyTimeHeat(double maxSelf)
    {
        if (HasTiming)
        {
            BarWidth = Math.Clamp(SelfTimeMs / maxSelf, 0, 1) * MaxBarWidth;
            IsBottleneck = SelfTimeMs >= maxSelf;
        }

        foreach (var child in Children)
        {
            child.ApplyTimeHeat(maxSelf);
        }
    }

    private double MaxSelfTime()
    {
        var max = SelfTimeMs;
        foreach (var child in Children)
        {
            max = Math.Max(max, child.MaxSelfTime());
        }

        return max;
    }

    private static double InclusiveMs(ExplainNode node) =>
        node.ActualTotalTimeMs is { } total ? total * (node.ActualLoops ?? 1) : 0;
}
