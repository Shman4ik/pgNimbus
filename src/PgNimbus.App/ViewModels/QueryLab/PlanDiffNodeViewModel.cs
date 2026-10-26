using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.App.ViewModels;

/// <summary>One figure of a plan node on both sides, for the details strip under the plan tree.</summary>
public sealed record NodeFigure(string Label, string Before, string After);

/// <summary>
/// A row of Query Lab's merged plan tree. The change is spelled out in a glyph and a
/// word, never in colour alone, and the row says what the node measured on each side.
/// </summary>
public sealed partial class PlanDiffNodeViewModel : ObservableObject
{
    public PlanDiffNodeViewModel(PlanDiffNode node)
    {
        Node = node;
        Children = [.. node.Children.Select(c => new PlanDiffNodeViewModel(c))];

        // Open where something changed, so the tree opens on the differences; a run of
        // same nodes, and a folded run of partition scans, stays closed.
        IsExpanded = node.Folded == 0 && Children.Any(c => c.Node.Change != PlanNodeChange.Same || c.Node.Moved || c.IsExpanded);
    }

    public PlanDiffNode Node { get; }

    public IReadOnlyList<PlanDiffNodeViewModel> Children { get; }

    /// <summary>Two-way with the tree row: a virtualizing tree reuses rows for other nodes.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public PlanNodeChange Change => Node.Change;

    public string Glyph => Node.Change switch
    {
        PlanNodeChange.Changed => "~",
        PlanNodeChange.Added => "+",
        PlanNodeChange.Removed => "−",
        _ => Node.Moved ? "↷" : "=",
    };

    public string ChangeWord
    {
        get
        {
            var word = Node.Change switch
            {
                PlanNodeChange.Changed => "Changed",
                PlanNodeChange.Added => "Added",
                PlanNodeChange.Removed => "Removed",
                _ => Node.Moved ? "Moved" : "Same",
            };

            return Node.Moved && Node.Change != PlanNodeChange.Same ? $"{word}, moved" : word;
        }
    }

    public bool IsChanged => Node.Change == PlanNodeChange.Changed;

    public bool IsAdded => Node.Change == PlanNodeChange.Added;

    public bool IsRemoved => Node.Change == PlanNodeChange.Removed;

    public bool IsSame => Node.Change == PlanNodeChange.Same && !Node.Moved;

    public string Header => Node.Header;

    /// <summary>What the node was before, when it was another kind of node ("was Seq Scan on orders o").</summary>
    public string? WasHeader =>
        Node is { Change: PlanNodeChange.Changed, Before: { } before, After: { } after }
        && ExplainTextFormatter.HeaderFor(before) is var was && was != ExplainTextFormatter.HeaderFor(after)
            ? $"was {was}"
            : null;

    public bool HasWasHeader => WasHeader is not null;

    /// <summary>The glyph and the word as one run of text, so they share a baseline: "~ Changed".</summary>
    public string Badge => $"{Glyph}  {ChangeWord}";

    /// <summary>The node's figures on one dim line: rows, self time, buffers, spill.</summary>
    public string MetricLine
    {
        get
        {
            if (Node.Folded > 0)
            {
                return string.Empty;
            }

            var a = Node.BeforeMetrics;
            var b = Node.AfterMetrics;
            var parts = new List<string>();
            var rows = Pair(a, b, m => m.ActualRowsTotal ?? m.EstimatedRows, PlanMetricUnit.Rows);
            parts.Add((a?.Analyzed ?? b?.Analyzed ?? false ? "rows " : "est. rows ") + rows);
            if (a?.SelfMs is not null || b?.SelfMs is not null)
            {
                var approximate = (a?.TimeApproximate ?? false) || (b?.TimeApproximate ?? false) ? "~" : string.Empty;
                parts.Add($"self {approximate}" + Pair(a, b, m => m.SelfMs, PlanMetricUnit.Milliseconds));
            }

            if (a?.Buffers is not null || b?.Buffers is not null)
            {
                parts.Add("hit " + Pair(a, b, m => m.Buffers?.SharedHit, PlanMetricUnit.Blocks));
                if ((a?.Buffers?.SharedRead ?? 0) > 0 || (b?.Buffers?.SharedRead ?? 0) > 0)
                {
                    parts.Add("read " + Pair(a, b, m => m.Buffers?.SharedRead, PlanMetricUnit.Blocks));
                }
            }

            if (a?.Spill is not null || b?.Spill is not null)
            {
                parts.Add($"disk: {a?.Spill ?? "none"} → {b?.Spill ?? "none"}");
            }

            if ((a?.NeverExecuted ?? false) || (b?.NeverExecuted ?? false))
            {
                parts.Add(a?.NeverExecuted == true && b?.NeverExecuted == true ? "never executed"
                    : a?.NeverExecuted == true ? "never executed before" : "never executed after");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>"40 → 40" for a matched pair, "40" for a node on one side only.</summary>
    private string Pair(PlanNodeMetrics? a, PlanNodeMetrics? b, Func<PlanNodeMetrics, double?> pick, PlanMetricUnit unit)
    {
        string Text(PlanNodeMetrics? m) => m is not null && pick(m) is { } value ? PlanMetricRow.Format(value, unit) : "—";
        return Node.Change switch
        {
            PlanNodeChange.Added => Text(b),
            PlanNodeChange.Removed => Text(a),
            _ => $"{Text(a)} → {Text(b)}",
        };
    }

    /// <summary>The differences in what the node does, one per line.</summary>
    public IReadOnlyList<string> Differences => Node.Differences;

    public bool HasDifferences => Node.Differences.Count > 0;

    /// <summary>Every figure of the node on both sides, for the details strip.</summary>
    public IReadOnlyList<NodeFigure> Figures
    {
        get
        {
            var a = Node.BeforeMetrics;
            var b = Node.AfterMetrics;
            if (a is null && b is null)
            {
                return [];
            }

            var figures = new List<NodeFigure>();
            void Add(string label, Func<PlanNodeMetrics, double?> pick, PlanMetricUnit unit)
            {
                string Text(PlanNodeMetrics? m) =>
                    m is null ? "—" : pick(m) is { } value ? PlanMetricRow.Format(value, unit) : "—";
                figures.Add(new NodeFigure(label, Text(a), Text(b)));
            }

            Add("Estimated rows (per loop)", m => m.EstimatedRows, PlanMetricUnit.Rows);
            Add("Actual rows (per loop)", m => m.ActualRows, PlanMetricUnit.Rows);
            Add("Loops", m => m.Loops, PlanMetricUnit.Count);
            Add("Time, with children", m => m.InclusiveMs, PlanMetricUnit.Milliseconds);
            Add("Self time", m => m.SelfMs, PlanMetricUnit.Milliseconds);
            Add("Estimated cost (planner units)", m => m.TotalCost, PlanMetricUnit.PlannerUnits);
            Add("Shared buffers hit", m => m.Buffers?.SharedHit, PlanMetricUnit.Blocks);
            Add("Shared buffers read", m => m.Buffers?.SharedRead, PlanMetricUnit.Blocks);
            if ((a?.Buffers?.TempWritten ?? 0) > 0 || (b?.Buffers?.TempWritten ?? 0) > 0)
            {
                Add("Temp blocks written", m => m.Buffers?.TempWritten, PlanMetricUnit.Blocks);
            }

            figures.Add(new NodeFigure("Went to disk", a?.Spill ?? (a is null ? "—" : "no"), b?.Spill ?? (b is null ? "—" : "no")));
            return figures;
        }
    }
}
