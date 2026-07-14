using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Presentation wrapper around a single <see cref="ExplainNode"/>: formats the
/// cost/row/timing figures Postgres reports and derives a bar width (relative
/// to the plan's total cost) so the tree reads as a rough visual profile, not
/// just a wall of numbers.
/// </summary>
public sealed class ExplainNodeViewModel
{
    private const double MaxBarWidth = 200;

    public ExplainNodeViewModel(ExplainNode node, double rootTotalCost)
    {
        Node = node;
        Children = node.Children.Select(c => new ExplainNodeViewModel(c, rootTotalCost)).ToList();
        BarWidth = rootTotalCost > 0 ? Math.Clamp(node.TotalCost / rootTotalCost, 0, 1) * MaxBarWidth : 0;
    }

    public ExplainNode Node { get; }

    public IReadOnlyList<ExplainNodeViewModel> Children { get; }

    public double BarWidth { get; }

    public string Title => ExplainTextFormatter.HeaderFor(Node);

    public string CostLabel => $"cost={Node.StartupCost:F2}..{Node.TotalCost:F2} rows={Node.PlanRows} width={Node.PlanWidth}";

    public string? ActualLabel => Node.ActualTotalTimeMs is { } total
        ? $"actual time={Node.ActualStartupTimeMs:F3}..{total:F3} ms rows={Node.ActualRows:0.##} loops={Node.ActualLoops}"
        : null;
}
