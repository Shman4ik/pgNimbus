using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Localization;
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

    /// <summary>Raises the text that is composed from the language, after a change of language.</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(ChangeWord));
        OnPropertyChanged(nameof(WasHeader));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(MetricLine));
        OnPropertyChanged(nameof(Figures));
        foreach (var child in Children)
        {
            child.RefreshText();
        }
    }

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
            // A word per case, not a word with ", moved" added: each language writes its own.
            return (Node.Change, Node.Moved) switch
            {
                (PlanNodeChange.Changed, true) => Strings.QueryLab_NodeChangedMoved,
                (PlanNodeChange.Changed, _) => Strings.QueryLab_NodeChanged,
                (PlanNodeChange.Added, true) => Strings.QueryLab_NodeAddedMoved,
                (PlanNodeChange.Added, _) => Strings.QueryLab_NodeAdded,
                (PlanNodeChange.Removed, true) => Strings.QueryLab_NodeRemovedMoved,
                (PlanNodeChange.Removed, _) => Strings.QueryLab_NodeRemoved,
                (_, true) => Strings.QueryLab_NodeMoved,
                _ => Strings.QueryLab_NodeSame,
            };
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
            ? Strings.QueryLab_WasHeader(was)
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
            parts.Add(a?.Analyzed ?? b?.Analyzed ?? false ? Strings.QueryLab_LineRows(rows) : Strings.QueryLab_LineEstRows(rows));
            if (a?.SelfMs is not null || b?.SelfMs is not null)
            {
                var approximate = (a?.TimeApproximate ?? false) || (b?.TimeApproximate ?? false) ? "~" : string.Empty;
                parts.Add(Strings.QueryLab_LineSelf(approximate + Pair(a, b, m => m.SelfMs, PlanMetricUnit.Milliseconds)));
            }

            if (a?.Buffers is not null || b?.Buffers is not null)
            {
                parts.Add(Strings.QueryLab_LineHit(Pair(a, b, m => m.Buffers?.SharedHit, PlanMetricUnit.Blocks)));
                if ((a?.Buffers?.SharedRead ?? 0) > 0 || (b?.Buffers?.SharedRead ?? 0) > 0)
                {
                    parts.Add(Strings.QueryLab_LineRead(Pair(a, b, m => m.Buffers?.SharedRead, PlanMetricUnit.Blocks)));
                }
            }

            if (a?.Spill is not null || b?.Spill is not null)
            {
                parts.Add(Strings.QueryLab_LineDisk(a?.Spill ?? Strings.QueryLab_None, b?.Spill ?? Strings.QueryLab_None));
            }

            if ((a?.NeverExecuted ?? false) || (b?.NeverExecuted ?? false))
            {
                parts.Add(a?.NeverExecuted == true && b?.NeverExecuted == true ? Strings.QueryLab_NeverExecuted
                    : a?.NeverExecuted == true ? Strings.QueryLab_NeverExecutedBefore : Strings.QueryLab_NeverExecutedAfter);
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

            Add(Strings.QueryLab_FigEstRows, m => m.EstimatedRows, PlanMetricUnit.Rows);
            Add(Strings.QueryLab_FigActRows, m => m.ActualRows, PlanMetricUnit.Rows);
            Add(Strings.QueryLab_FigLoops, m => m.Loops, PlanMetricUnit.Count);
            Add(Strings.QueryLab_FigInclusiveTime, m => m.InclusiveMs, PlanMetricUnit.Milliseconds);
            Add(Strings.QueryLab_FigSelfTime, m => m.SelfMs, PlanMetricUnit.Milliseconds);
            Add(Strings.QueryLab_FigCost, m => m.TotalCost, PlanMetricUnit.PlannerUnits);
            Add(Strings.QueryLab_FigBuffersHit, m => m.Buffers?.SharedHit, PlanMetricUnit.Blocks);
            Add(Strings.QueryLab_FigBuffersRead, m => m.Buffers?.SharedRead, PlanMetricUnit.Blocks);
            if ((a?.Buffers?.TempWritten ?? 0) > 0 || (b?.Buffers?.TempWritten ?? 0) > 0)
            {
                Add(Strings.QueryLab_FigTempWritten, m => m.Buffers?.TempWritten, PlanMetricUnit.Blocks);
            }

            figures.Add(new NodeFigure(Strings.QueryLab_FigDisk, a?.Spill ?? (a is null ? "—" : Strings.QueryLab_No), b?.Spill ?? (b is null ? "—" : Strings.QueryLab_No)));
            return figures;
        }
    }
}
