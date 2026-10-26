using System.Globalization;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.QueryLab;

/// <summary>What happened to a plan node between two plans.</summary>
public enum PlanNodeChange
{
    /// <summary>The same operation on the same relation, with the same conditions.</summary>
    Same,

    /// <summary>The same node doing something else: another scan type, index, join method, condition.</summary>
    Changed,

    /// <summary>Only in the after plan.</summary>
    Added,

    /// <summary>Only in the before plan.</summary>
    Removed,
}

/// <summary>
/// A row of the merged plan tree: a node of either plan, or both when they correspond.
/// </summary>
public sealed record PlanDiffNode(
    PlanNodeChange Change,
    ExplainNode? Before,
    ExplainNode? After,
    PlanNodeMetrics? BeforeMetrics,
    PlanNodeMetrics? AfterMetrics,
    IReadOnlyList<string> Differences,
    IReadOnlyList<PlanDiffNode> Children)
{
    /// <summary>
    /// The node's header in EXPLAIN's text layout: the after plan's when there is one,
    /// or what a folded row stands for ("312 partition scans").
    /// </summary>
    public string Header { get; init; } =
        After is { } after ? ExplainTextFormatter.HeaderFor(after)
        : Before is { } before ? ExplainTextFormatter.HeaderFor(before)
        : string.Empty;

    /// <summary>
    /// The node hangs under a different parent than before (a scan that moved to the
    /// other side of a join, or under a new Gather).
    /// </summary>
    public bool Moved { get; init; }

    /// <summary>A join whose outer and inner inputs changed sides.</summary>
    public bool InputsSwapped { get; init; }

    /// <summary>
    /// When non-zero, this row stands for that many sibling scans that were all added
    /// or all removed (a partition list after pruning changed); they are its children.
    /// </summary>
    public int Folded { get; init; }
}

/// <summary>The merged tree of two plans and how many nodes changed in each way.</summary>
public sealed record PlanDiffResult(
    IReadOnlyList<PlanDiffNode> Roots,
    int Same,
    int Changed,
    int Added,
    int Removed,
    int Moved)
{
    /// <summary>True when the plan's shape is not what it was: a node came, went, or moved.</summary>
    public bool ShapeChanged => Added > 0 || Removed > 0 || Moved > 0;
}

/// <summary>
/// Builds the merged tree of two aligned plans. The after plan is the skeleton, since
/// it is the plan as it stands now; each removed node is grafted under the counterpart of
/// its nearest matched ancestor, in the place it held among its siblings. Core-pure and
/// deterministic.
/// </summary>
public static class PlanDiff
{
    /// <summary>Sibling scans that were all added or all removed fold into one row from this many on.</summary>
    public const int FoldThreshold = 5;

    /// <summary>
    /// Detail lines that say what a node does, as opposed to what it measured: a
    /// difference in one of these changes the node. Row counts removed by a filter,
    /// heap fetches and memory are figures, compared as metrics instead.
    /// </summary>
    private static readonly HashSet<string> ConditionKeys =
    [
        "Filter", "Index Cond", "Recheck Cond", "Join Filter", "Hash Cond", "Merge Cond", "TID Cond",
        "One-Time Filter", "Sort Key", "Presorted Key", "Group Key", "Cache Key", "Order By",
        "Cache Mode", "Command", "Conflict Resolution", "Conflict Arbiter Indexes", "Run Condition",
    ];

    public static PlanDiffResult Build(ExplainResult before, ExplainResult after) =>
        Build(PlanAlignment.Align(before, after), PlanMetrics.Compute(before), PlanMetrics.Compute(after));

    public static PlanDiffResult Build(
        PlanAlignment alignment,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> beforeMetrics,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> afterMetrics)
    {
        var builder = new Builder(alignment, beforeMetrics, afterMetrics);
        return builder.Run();
    }

    private sealed class Builder(
        PlanAlignment alignment,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> beforeMetrics,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> afterMetrics)
    {
        private readonly PlanTree _before = alignment.BeforeTree;
        private readonly PlanTree _after = alignment.AfterTree;
        private int _same;
        private int _changed;
        private int _added;
        private int _removed;
        private int _moved;

        public PlanDiffResult Run()
        {
            // Removed nodes hang off the after node their nearest matched ancestor became,
            // or off their removed parent; the ones with neither sit beside the root.
            var graftedUnderAfter = new Dictionary<int, List<int>>();
            var topRemoved = new List<int>();
            for (var i = 0; i < _before.Count; i++)
            {
                if (alignment.AfterIndexOf(i) >= 0)
                {
                    continue;
                }

                var parent = _before.Parents[i];
                if (parent < 0)
                {
                    topRemoved.Add(i);
                }
                else if (alignment.AfterIndexOf(parent) is var image and >= 0)
                {
                    if (!graftedUnderAfter.TryGetValue(image, out var list))
                    {
                        graftedUnderAfter[image] = list = [];
                    }

                    list.Add(i);
                }
            }

            var roots = new List<PlanDiffNode> { AfterNode(0, graftedUnderAfter) };
            roots.AddRange(topRemoved.Select(RemovedNode));
            return new PlanDiffResult(roots, _same, _changed, _added, _removed, _moved);
        }

        private PlanDiffNode AfterNode(int index, Dictionary<int, List<int>> grafted)
        {
            var node = _after.Nodes[index];
            var children = Fold(Children(index, grafted));
            var beforeIndex = alignment.BeforeIndexOf(index);
            if (beforeIndex < 0)
            {
                _added++;
                return new PlanDiffNode(PlanNodeChange.Added, null, node, null, Metrics(afterMetrics, node), [], children);
            }

            var before = _before.Nodes[beforeIndex];
            var differences = Differences(beforeIndex, index);
            var swapped = InputsSwapped(beforeIndex, index);
            if (swapped)
            {
                differences.Add("outer and inner inputs swapped");
            }

            var beforeParent = _before.Parents[beforeIndex];
            var moved = beforeParent >= 0
                && alignment.AfterIndexOf(beforeParent) is var parentImage and >= 0
                && parentImage != _after.Parents[index];
            if (moved)
            {
                _moved++;
            }

            var change = differences.Count > 0 ? PlanNodeChange.Changed : PlanNodeChange.Same;
            if (change == PlanNodeChange.Changed)
            {
                _changed++;
            }
            else
            {
                _same++;
            }

            return new PlanDiffNode(
                change, before, node, Metrics(beforeMetrics, before), Metrics(afterMetrics, node), differences, children)
            {
                Moved = moved,
                InputsSwapped = swapped,
            };
        }

        /// <summary>
        /// The after node's own children, with the removed nodes that used to hang under its
        /// counterpart placed after the sibling that preceded them in the before plan.
        /// </summary>
        private List<PlanDiffNode> Children(int index, Dictionary<int, List<int>> grafted)
        {
            var afterChildren = _after.Children[index];
            var own = afterChildren.Select(c => AfterNode(c, grafted)).ToList();
            if (!grafted.TryGetValue(index, out var removed))
            {
                return own;
            }

            // One pass over the before siblings: each removed node goes after the last
            // earlier sibling whose counterpart is one of these children (slot 0 when
            // none is). All the grafted nodes share the before parent, so this is linear
            // even for an Append that lost thousands of partitions.
            var position = new Dictionary<int, int>();
            for (var p = 0; p < afterChildren.Length; p++)
            {
                position[afterChildren[p]] = p;
            }

            var slots = new List<int>[afterChildren.Length + 1];
            var grafts = removed.ToHashSet();
            var anchor = 0;
            foreach (var sibling in _before.Children[_before.Parents[removed[0]]])
            {
                if (alignment.AfterIndexOf(sibling) is var image and >= 0 && position.TryGetValue(image, out var p))
                {
                    anchor = p + 1;
                }
                else if (grafts.Contains(sibling))
                {
                    (slots[anchor] ??= []).Add(sibling);
                }
            }

            var children = new List<PlanDiffNode>(own.Count + removed.Count);
            for (var slot = 0; slot <= afterChildren.Length; slot++)
            {
                if (slot > 0)
                {
                    children.Add(own[slot - 1]);
                }

                if (slots[slot] is { } placed)
                {
                    children.AddRange(placed.Select(RemovedNode));
                }
            }

            return children;
        }

        private PlanDiffNode RemovedNode(int index)
        {
            _removed++;
            var node = _before.Nodes[index];
            var children = _before.Children[index]
                .Where(c => alignment.AfterIndexOf(c) < 0)
                .Select(RemovedNode)
                .ToList();
            return new PlanDiffNode(PlanNodeChange.Removed, node, null, Metrics(beforeMetrics, node), null, [], Fold(children));
        }

        /// <summary>
        /// Folds each run of <see cref="FoldThreshold"/> or more sibling scans that were all
        /// added or all removed into one row (a partition list that pruning changed),
        /// leaving the nodes as the row's children.
        /// </summary>
        private static List<PlanDiffNode> Fold(List<PlanDiffNode> children)
        {
            if (children.Count < FoldThreshold)
            {
                return children;
            }

            var folded = new List<PlanDiffNode>();
            for (var start = 0; start < children.Count;)
            {
                var change = children[start].Change;
                var end = start;
                while (end < children.Count && children[end].Change == change && IsLeafScan(children[end])
                       && change is PlanNodeChange.Added or PlanNodeChange.Removed)
                {
                    end++;
                }

                if (end - start >= FoldThreshold)
                {
                    var run = children.GetRange(start, end - start);
                    var word = change == PlanNodeChange.Added ? "added" : "removed";
                    folded.Add(new PlanDiffNode(change, null, null, null, null, [], run)
                    {
                        Header = string.Create(CultureInfo.InvariantCulture, $"{run.Count:N0} scans {word}"),
                        Folded = run.Count,
                    });
                    start = end;
                }
                else
                {
                    folded.Add(children[start]);
                    start++;
                }
            }

            return folded;
        }

        private static bool IsLeafScan(PlanDiffNode node) =>
            node.Children.Count == 0
            && (node.After ?? node.Before) is { } plan
            && PlanNodeLabel.Of(plan).Family == PlanNodeFamily.Scan;

        private List<string> Differences(int before, int after)
        {
            var a = _before.Labels[before];
            var b = _after.Labels[after];
            var differences = new List<string>();
            if (a.Type != b.Type)
            {
                differences.Add($"{a.Type} → {b.Type}");
            }

            Compare("join type", a.JoinType, b.JoinType);
            Compare("strategy", a.Strategy, b.Strategy);
            Compare("mode", a.PartialMode ?? "Simple", b.PartialMode ?? "Simple");
            Compare("operation", a.Operation, b.Operation);
            Compare("index", a.Index, b.Index);
            Compare("direction", a.Direction ?? "Forward", b.Direction ?? "Forward");
            if (a.Parallel != b.Parallel)
            {
                differences.Add(b.Parallel ? "now parallel" : "no longer parallel");
            }

            var beforeDetails = Conditions(_before.Nodes[before]);
            var afterDetails = Conditions(_after.Nodes[after]);
            foreach (var (key, value) in beforeDetails)
            {
                if (!afterDetails.TryGetValue(key, out var now))
                {
                    differences.Add($"{key} removed: {value}");
                }
                else if (now != value)
                {
                    differences.Add($"{key}: {value} → {now}");
                }
            }

            foreach (var (key, value) in afterDetails)
            {
                if (!beforeDetails.ContainsKey(key))
                {
                    differences.Add($"{key} added: {value}");
                }
            }

            return differences;

            void Compare(string what, string? was, string? now)
            {
                if (was != now)
                {
                    differences.Add($"{what}: {was ?? "none"} → {now ?? "none"}");
                }
            }
        }

        private static SortedDictionary<string, string> Conditions(ExplainNode node)
        {
            var conditions = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in node.Details)
            {
                if (ConditionKeys.Contains(key))
                {
                    conditions[key] = value;
                }
            }

            return conditions;
        }

        /// <summary>
        /// A two-input join whose inputs changed sides: the relations its first input read
        /// are now read by its second, and the other way round. Only scans of named
        /// relations count, since they are what flows into the join.
        /// </summary>
        private bool InputsSwapped(int before, int after)
        {
            if (_before.Labels[before].Family != PlanNodeFamily.Join
                || _before.Children[before].Length != 2 || _after.Children[after].Length != 2)
            {
                return false;
            }

            return Dominant(_before.Children[before][0], after) == 1 && Dominant(_before.Children[before][1], after) == 0;
        }

        /// <summary>Which input of the after join holds most of the relations a before input read; -1 when none.</summary>
        private int Dominant(int beforeChild, int afterJoin)
        {
            var votes = new int[2];
            for (var d = beforeChild; d < beforeChild + _before.Sizes[beforeChild]; d++)
            {
                if (_before.Labels[d].Relation is null || alignment.AfterIndexOf(d) is not (var image and >= 0))
                {
                    continue;
                }

                for (var side = 0; side < 2; side++)
                {
                    var root = _after.Children[afterJoin][side];
                    if (image >= root && image < root + _after.Sizes[root])
                    {
                        votes[side]++;
                    }
                }
            }

            return votes[0] == votes[1] ? -1 : votes[0] > votes[1] ? 0 : 1;
        }

        private static PlanNodeMetrics? Metrics(IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> map, ExplainNode node) =>
            map.TryGetValue(node, out var metrics) ? metrics : null;
    }
}
