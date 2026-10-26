using PgNimbus.Core.Query;

namespace PgNimbus.Core.QueryLab;

/// <summary>
/// Which node of one plan is which node of another: the mapping a plan comparison is
/// built on. Core-pure and deterministic, a sibling of <see cref="PlanAnalyzer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Anchor matching in the GumTree style rather than a tree edit distance. An ordered
/// edit distance pairs nodes by position, and the planner moves things: it swaps a hash
/// join's inputs, puts a Gather above a scan, prunes a different set of partitions. Each
/// of those read as a subtree deleted and re-inserted. Anchors read them as what they
/// are, and stay near-linear on a plan over thousands of partitions.
/// </para>
/// <para>
/// Four passes, each only over nodes the earlier ones left unmatched:
/// (1) identical subtrees whose shape occurs once in each plan, tallest first;
/// (2) scans by the relation they read (with the alias, then by the relation alone when
/// it is read once on each side), whatever the scan type, so a Seq Scan that became an
/// Index Scan pairs up; an alias alone never anchors, since partition aliases renumber
/// when pruning changes;
/// (3) containers bottom-up, each taking the candidate of its family that holds most of
/// its matched descendants (Dice ≥ 0.5);
/// (4) under every matched pair, an LCS over the still-unmatched children, exact labels
/// first and then the family (a Hash, a Sort, a Gather, a Bitmap Index Scan).
/// </para>
/// </remarks>
public sealed class PlanAlignment
{
    private const double MinContainerDice = 0.5;

    // Above this many cells an LCS of two child lists falls back to a single in-order pass.
    private const long MaxLcsCells = 1_000_000;

    private readonly PlanTree _before;
    private readonly PlanTree _after;
    private readonly int[] _beforeToAfter;
    private readonly int[] _afterToBefore;

    private PlanAlignment(PlanTree before, PlanTree after)
    {
        _before = before;
        _after = after;
        _beforeToAfter = Enumerable.Repeat(-1, before.Count).ToArray();
        _afterToBefore = Enumerable.Repeat(-1, after.Count).ToArray();
    }

    internal PlanTree BeforeTree => _before;

    internal PlanTree AfterTree => _after;

    /// <summary>The matched (before, after) pairs, in the before plan's preorder.</summary>
    public IReadOnlyList<(ExplainNode Before, ExplainNode After)> Pairs =>
        [.. Enumerable.Range(0, _before.Count)
            .Where(i => _beforeToAfter[i] >= 0)
            .Select(i => (_before.Nodes[i], _after.Nodes[_beforeToAfter[i]]))];

    /// <summary>The after plan's counterpart of a before node, or null when it was removed.</summary>
    public ExplainNode? AfterOf(ExplainNode before) =>
        _before.IndexOf(before) is var i and >= 0 && _beforeToAfter[i] >= 0 ? _after.Nodes[_beforeToAfter[i]] : null;

    /// <summary>The before plan's counterpart of an after node, or null when it was added.</summary>
    public ExplainNode? BeforeOf(ExplainNode after) =>
        _after.IndexOf(after) is var i and >= 0 && _afterToBefore[i] >= 0 ? _before.Nodes[_afterToBefore[i]] : null;

    internal int AfterIndexOf(int beforeIndex) => _beforeToAfter[beforeIndex];

    internal int BeforeIndexOf(int afterIndex) => _afterToBefore[afterIndex];

    public static PlanAlignment Align(ExplainResult before, ExplainResult after)
    {
        var alignment = new PlanAlignment(
            new PlanTree(before.Root, before.Capture.Format),
            new PlanTree(after.Root, after.Capture.Format));
        alignment.MatchIdenticalSubtrees();
        alignment.MatchScanAnchors();
        alignment.MatchContainers();
        alignment.MatchRoots();
        alignment.Recover();
        return alignment;
    }

    private void Match(int before, int after)
    {
        _beforeToAfter[before] = after;
        _afterToBefore[after] = before;
    }

    private bool IsFree(int before, int after) => _beforeToAfter[before] < 0 && _afterToBefore[after] < 0;

    // --- (1) identical subtrees ---

    private void MatchIdenticalSubtrees()
    {
        var beforeCounts = Count(_before.Hashes);
        var afterByHash = new Dictionary<ulong, int>();
        var afterCounts = Count(_after.Hashes);
        for (var i = 0; i < _after.Count; i++)
        {
            if (afterCounts[_after.Hashes[i]] == 1)
            {
                afterByHash[_after.Hashes[i]] = i;
            }
        }

        var candidates = new List<(int Before, int After)>();
        for (var i = 0; i < _before.Count; i++)
        {
            var hash = _before.Hashes[i];
            if (beforeCounts[hash] == 1 && afterByHash.TryGetValue(hash, out var j))
            {
                candidates.Add((i, j));
            }
        }

        // Tallest first, so a whole matched subtree claims its descendants before they
        // can be paired with lookalikes elsewhere; then preorder, for a stable result.
        candidates.Sort((x, y) =>
        {
            var byHeight = _before.Heights[y.Before].CompareTo(_before.Heights[x.Before]);
            return byHeight != 0 ? byHeight : x.Before.CompareTo(y.Before);
        });

        foreach (var (i, j) in candidates)
        {
            if (IsFree(i, j) && SameShape(i, j))
            {
                MatchSubtree(i, j);
            }
        }
    }

    private static Dictionary<ulong, int> Count(ulong[] hashes)
    {
        var counts = new Dictionary<ulong, int>();
        foreach (var hash in hashes)
        {
            counts[hash] = counts.GetValueOrDefault(hash) + 1;
        }

        return counts;
    }

    /// <summary>A hash match is checked, not trusted: two subtrees are the same shape when every label agrees.</summary>
    private bool SameShape(int before, int after)
    {
        if (_before.Labels[before] != _after.Labels[after]
            || _before.Children[before].Length != _after.Children[after].Length)
        {
            return false;
        }

        for (var k = 0; k < _before.Children[before].Length; k++)
        {
            if (!SameShape(_before.Children[before][k], _after.Children[after][k]))
            {
                return false;
            }
        }

        return true;
    }

    private void MatchSubtree(int before, int after)
    {
        if (!IsFree(before, after))
        {
            return;
        }

        Match(before, after);
        for (var k = 0; k < _before.Children[before].Length; k++)
        {
            MatchSubtree(_before.Children[before][k], _after.Children[after][k]);
        }
    }

    // --- (2) scans by relation ---

    private void MatchScanAnchors()
    {
        // With the alias first: a self-join reads one relation twice under two aliases.
        MatchByKey(label => label.Relation is { } relation ? $"{Kind(label)}\u0001{relation}\u0001{label.Alias}" : null);
        MatchByKey(label => label.Relation is { } relation ? $"{Kind(label)}\u0001{relation}" : null);
    }

    /// <summary>A table being written and a table being read are never the same node.</summary>
    private static string Kind(PlanNodeLabel label) => label.Family == PlanNodeFamily.Modify ? "write" : "read";

    private void MatchByKey(Func<PlanNodeLabel, string?> key)
    {
        var beforeKeys = Keys(_before, _beforeToAfter, key);
        var afterKeys = Keys(_after, _afterToBefore, key);
        for (var i = 0; i < _before.Count; i++)
        {
            if (_beforeToAfter[i] < 0 && key(_before.Labels[i]) is { } k
                && beforeKeys.TryGetValue(k, out var mine) && mine.Count == 1
                && afterKeys.TryGetValue(k, out var theirs) && theirs.Count == 1
                && _afterToBefore[theirs[0]] < 0)
            {
                Match(i, theirs[0]);
            }
        }
    }

    private static Dictionary<string, List<int>> Keys(PlanTree tree, int[] matched, Func<PlanNodeLabel, string?> key)
    {
        var keys = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < tree.Count; i++)
        {
            if (matched[i] < 0 && key(tree.Labels[i]) is { } k)
            {
                if (!keys.TryGetValue(k, out var list))
                {
                    keys[k] = list = [];
                }

                list.Add(i);
            }
        }

        return keys;
    }

    // --- (3) containers bottom-up ---

    private void MatchContainers()
    {
        // Descending preorder visits every descendant before its ancestor.
        for (var i = _before.Count - 1; i >= 0; i--)
        {
            if (_beforeToAfter[i] >= 0 || _before.Sizes[i] == 1)
            {
                continue;
            }

            var family = _before.Labels[i].Family;
            var common = new Dictionary<int, int>();
            for (var d = i + 1; d < i + _before.Sizes[i]; d++)
            {
                if (_beforeToAfter[d] is var image and >= 0)
                {
                    for (var c = _after.Parents[image]; c >= 0; c = _after.Parents[c])
                    {
                        if (_afterToBefore[c] < 0 && _after.Labels[c].Family == family)
                        {
                            common[c] = common.GetValueOrDefault(c) + 1;
                        }
                    }
                }
            }

            var best = -1;
            var bestDice = 0.0;
            foreach (var (candidate, shared) in common.OrderBy(pair => pair.Key))
            {
                var dice = 2.0 * shared / ((_before.Sizes[i] - 1) + (_after.Sizes[candidate] - 1));
                if (dice > bestDice)
                {
                    (best, bestDice) = (candidate, dice);
                }
            }

            if (best >= 0 && bestDice >= MinContainerDice)
            {
                Match(i, best);
            }
        }
    }

    /// <summary>The two statements' top nodes correspond when nothing else claimed them and they do the same kind of work.</summary>
    private void MatchRoots()
    {
        if (IsFree(0, 0) && _before.Labels[0].Family == _after.Labels[0].Family)
        {
            Match(0, 0);
        }
    }

    // --- (4) recovery under matched pairs ---

    private void Recover()
    {
        var queue = new Queue<(int Before, int After)>();
        for (var i = 0; i < _before.Count; i++)
        {
            if (_beforeToAfter[i] >= 0)
            {
                queue.Enqueue((i, _beforeToAfter[i]));
            }
        }

        while (queue.Count > 0)
        {
            var (before, after) = queue.Dequeue();
            foreach (var exact in (ReadOnlySpan<bool>)[true, false])
            {
                var mine = _before.Children[before].Where(c => _beforeToAfter[c] < 0).ToArray();
                var theirs = _after.Children[after].Where(c => _afterToBefore[c] < 0).ToArray();
                if (mine.Length == 0 || theirs.Length == 0)
                {
                    break;
                }

                foreach (var (i, j) in Lcs(mine, theirs, exact))
                {
                    Match(i, j);
                    queue.Enqueue((i, j));
                }
            }
        }
    }

    private bool Similar(int before, int after, bool exact)
    {
        var a = _before.Labels[before];
        var b = _after.Labels[after];
        return exact
            ? a == b
            : a.Family == b.Family && a.Relation == b.Relation && Kind(a) == Kind(b);
    }

    private List<(int Before, int After)> Lcs(int[] mine, int[] theirs, bool exact)
    {
        var pairs = new List<(int, int)>();
        if ((long)mine.Length * theirs.Length > MaxLcsCells)
        {
            // One in-order pass: enough for the partition lists this would otherwise square.
            var at = 0;
            foreach (var i in mine)
            {
                for (var j = at; j < theirs.Length; j++)
                {
                    if (Similar(i, theirs[j], exact))
                    {
                        pairs.Add((i, theirs[j]));
                        at = j + 1;
                        break;
                    }
                }
            }

            return pairs;
        }

        var table = new int[mine.Length + 1, theirs.Length + 1];
        for (var x = mine.Length - 1; x >= 0; x--)
        {
            for (var y = theirs.Length - 1; y >= 0; y--)
            {
                table[x, y] = Similar(mine[x], theirs[y], exact)
                    ? table[x + 1, y + 1] + 1
                    : Math.Max(table[x + 1, y], table[x, y + 1]);
            }
        }

        for (int x = 0, y = 0; x < mine.Length && y < theirs.Length;)
        {
            if (Similar(mine[x], theirs[y], exact) && table[x, y] == table[x + 1, y + 1] + 1)
            {
                pairs.Add((mine[x], theirs[y]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }
}

/// <summary>A plan flattened into preorder arrays, with each node's label, subtree size, height and shape hash.</summary>
internal sealed class PlanTree
{
    private readonly Dictionary<ExplainNode, int> _index = new(ReferenceEqualityComparer.Instance);

    public PlanTree(ExplainNode root, PlanFormat format)
    {
        var nodes = new List<ExplainNode>();
        var parents = new List<int>();
        Flatten(root, -1, nodes, parents);
        Nodes = [.. nodes];
        Parents = [.. parents];
        Count = Nodes.Length;
        Labels = new PlanNodeLabel[Count];
        Children = new int[Count][];
        Sizes = new int[Count];
        Heights = new int[Count];
        Hashes = new ulong[Count];
        var childLists = Enumerable.Range(0, Count).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < Count; i++)
        {
            _index[Nodes[i]] = i;
            Labels[i] = PlanNodeLabel.Of(Nodes[i], format);
            if (Parents[i] >= 0)
            {
                childLists[Parents[i]].Add(i);
            }
        }

        // Descending preorder: every child before its parent.
        for (var i = Count - 1; i >= 0; i--)
        {
            Children[i] = [.. childLists[i]];
            Sizes[i] = 1 + Children[i].Sum(c => Sizes[c]);
            Heights[i] = Children[i].Length == 0 ? 1 : 1 + Children[i].Max(c => Heights[c]);
            var hash = Fnv.Of(Labels[i]);
            foreach (var child in Children[i])
            {
                hash = Fnv.Combine(hash, Hashes[child]);
            }

            Hashes[i] = hash;
        }
    }

    public int Count { get; }

    public ExplainNode[] Nodes { get; }

    public int[] Parents { get; }

    public PlanNodeLabel[] Labels { get; }

    public int[][] Children { get; }

    public int[] Sizes { get; }

    public int[] Heights { get; }

    public ulong[] Hashes { get; }

    public int IndexOf(ExplainNode node) => _index.TryGetValue(node, out var i) ? i : -1;

    private static void Flatten(ExplainNode node, int parent, List<ExplainNode> nodes, List<int> parents)
    {
        var index = nodes.Count;
        nodes.Add(node);
        parents.Add(parent);
        foreach (var child in node.Children)
        {
            Flatten(child, index, nodes, parents);
        }
    }

    /// <summary>FNV-1a over a label's fields: stable across runs, unlike <see cref="string.GetHashCode()"/>.</summary>
    private static class Fnv
    {
        private const ulong Offset = 14695981039346656037;
        private const ulong Prime = 1099511628211;

        public static ulong Of(PlanNodeLabel label)
        {
            var hash = Offset;
            foreach (var field in (ReadOnlySpan<string?>)[label.Type, label.JoinType, label.Strategy, label.PartialMode,
                         label.Operation, label.Relation, label.Alias, label.Index, label.Direction, label.Subplan,
                         label.Parallel ? "parallel" : null])
            {
                hash = Mix(hash, field);
            }

            return hash;
        }

        public static ulong Combine(ulong hash, ulong child)
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                hash = (hash ^ ((child >> shift) & 0xFF)) * Prime;
            }

            return hash;
        }

        private static ulong Mix(ulong hash, string? field)
        {
            if (field is not null)
            {
                foreach (var c in field)
                {
                    hash = (hash ^ (c & 0xFFu)) * Prime;
                    hash = (hash ^ (uint)(c >> 8)) * Prime;
                }
            }

            // A separator, so ("ab", null) and ("a", "b") differ.
            return (hash ^ 0x1F) * Prime;
        }
    }
}
