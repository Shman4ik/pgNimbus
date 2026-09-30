namespace PgNimbus.Core.Monitoring;

/// <summary>
/// One backend that participates in a lock wait, as seen from
/// <c>pg_stat_activity</c> + <c>pg_blocking_pids()</c>. <paramref name="BlockedByPids"/>
/// is the set of pids currently holding a lock this backend is stuck waiting on
/// (empty for a backend that only blocks others). <paramref name="LockedObject"/>/
/// <paramref name="LockMode"/> describe the not-yet-granted lock the waiter wants.
/// </summary>
public sealed record BlockingBackend(
    int Pid,
    string? User,
    string? Database,
    string? Application,
    string State,
    string? WaitEventType,
    string? WaitEvent,
    double ElapsedSeconds,
    string Query,
    IReadOnlyList<int> BlockedByPids,
    string? LockedObject,
    string? LockMode)
{
    /// <summary>True when at least one other backend is waiting on a lock this one holds.</summary>
    public bool IsWaiting => BlockedByPids.Count > 0;
}

/// <summary>
/// One node in the who-blocks-whom forest the activity window renders: a backend
/// and the backends it is (directly) blocking. Roots are the ultimate lock
/// holders — the backends to cancel/terminate to unstick everyone beneath them.
/// Each backend appears exactly once in the forest; a waiter with several
/// blockers sits under one of them and names the rest in <see cref="AlsoBlockedBy"/>.
/// </summary>
public sealed record BlockingTreeNode(
    BlockingBackend Backend,
    IReadOnlyList<BlockingTreeNode> Children)
{
    /// <summary>
    /// The pids blocking this backend other than the node it sits under, ascending —
    /// for a root, every blocker it has (all of them outside the snapshot, or in a
    /// deadlock cycle). Empty for the common single-blocker waiter.
    /// </summary>
    public IReadOnlyList<int> AlsoBlockedBy { get; init; } = [];

    /// <summary>Total backends blocked somewhere below this one (whole subtree).</summary>
    public int BlockedDescendants
    {
        get
        {
            // The forest is a spanning tree (each backend once), so a plain count is
            // already deduplicated. Iterative: a wait chain is as long as the server's
            // connection limit.
            var count = 0;
            var stack = new Stack<BlockingTreeNode>();
            stack.Push(this);
            while (stack.TryPop(out var node))
            {
                foreach (var child in node.Children)
                {
                    count++;
                    stack.Push(child);
                }
            }

            return count;
        }
    }
}

/// <summary>
/// Turns a flat list of <see cref="BlockingBackend"/> rows into a forest of
/// blocker → blocked edges. Pure logic (Core stays Avalonia-free), unit-tested:
/// the App binds a <c>TreeView</c> to the roots. Robust to the awkward shapes a
/// live server produces — chains (A blocks B blocks C), a waiter with several
/// blockers, blockers that aren't in the snapshot (autovacuum), and even a
/// transient deadlock cycle (never walked twice, so it can't recurse forever).
/// </summary>
/// <remarks>
/// The forest is a <em>spanning</em> tree: every backend appears once, under the
/// first of its blockers a breadth-first walk from the roots reaches (roots and
/// siblings in pid order, so the shape is the same on every refresh), and names
/// its other blockers in <see cref="BlockingTreeNode.AlsoBlockedBy"/>. It used to
/// repeat a waiter under every blocker, subtree and all — and <c>pg_blocking_pids</c>
/// reports soft blocks too, so N sessions queued on one hot row form a complete DAG
/// with about 2^(N-3) paths: 25 waiters made ~4M nodes, 30 made ~134M, built on the
/// UI thread every 2 s during exactly the incident the tab exists for (security
/// audit 2026-09, finding 16).
/// </remarks>
public static class BlockingTree
{
    /// <summary>Roots of the blocking forest — the lock holders no one visible is waiting behind.</summary>
    public static IReadOnlyList<BlockingTreeNode> Build(IReadOnlyList<BlockingBackend> backends)
    {
        var byPid = new Dictionary<int, BlockingBackend>();
        foreach (var b in backends)
        {
            // Later duplicates (shouldn't happen from the query) just overwrite.
            byPid[b.Pid] = b;
        }

        // blocker pid -> pids it directly blocks (only edges where both ends are
        // in the snapshot; a blocker we can't see becomes an invisible-blocker case).
        var blocks = new Dictionary<int, SortedSet<int>>();
        foreach (var b in byPid.Values)
        {
            foreach (var blocker in b.BlockedByPids)
            {
                if (blocker != b.Pid && byPid.ContainsKey(blocker))
                {
                    (blocks.TryGetValue(blocker, out var set) ? set : blocks[blocker] = new SortedSet<int>()).Add(b.Pid);
                }
            }
        }

        // A pid is "involved" if it waits on something or blocks someone.
        bool Involved(BlockingBackend b) => b.BlockedByPids.Count > 0 || blocks.ContainsKey(b.Pid);

        // A pid has a *visible* blocker only if one of its blockers is in the snapshot.
        bool HasVisibleBlocker(BlockingBackend b) => b.BlockedByPids.Any(p => p != b.Pid && byPid.ContainsKey(p));

        var involved = byPid.Values.Where(Involved).OrderBy(b => b.Pid).ToList();
        var parent = new Dictionary<int, int?>();
        var childrenOf = new Dictionary<int, List<int>>();
        var rootPids = new List<int>();

        void Walk(int root)
        {
            parent[root] = null;
            rootPids.Add(root);
            var queue = new Queue<int>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var pid))
            {
                if (!blocks.TryGetValue(pid, out var blocked))
                {
                    continue;
                }

                foreach (var childPid in blocked)
                {
                    // Placed already (under an earlier blocker, or an ancestor closing
                    // a cycle): it stays where it is and lists this pid as another blocker.
                    if (parent.ContainsKey(childPid))
                    {
                        continue;
                    }

                    parent[childPid] = pid;
                    (childrenOf.TryGetValue(pid, out var list) ? list : childrenOf[pid] = []).Add(childPid);
                    queue.Enqueue(childPid);
                }
            }
        }

        // Primary roots: involved backends with no visible backend blocking them.
        foreach (var b in involved.Where(b => !HasVisibleBlocker(b)))
        {
            Walk(b.Pid);
        }

        // Anything involved but not yet placed sits inside a cycle (deadlock) —
        // promote its lowest pid to a root so it's never silently dropped.
        foreach (var b in involved)
        {
            if (!parent.ContainsKey(b.Pid))
            {
                Walk(b.Pid);
            }
        }

        // Records are immutable and point at their children, so build bottom-up:
        // children lists are filled after their owners exist (no recursion, since a
        // chain can be as long as the connection limit).
        var nodes = new Dictionary<int, BlockingTreeNode>();
        var lists = new Dictionary<int, List<BlockingTreeNode>>();
        foreach (var (pid, parentPid) in parent)
        {
            var backend = byPid[pid];
            var children = new List<BlockingTreeNode>();
            lists[pid] = children;
            nodes[pid] = new BlockingTreeNode(backend, children)
            {
                AlsoBlockedBy = backend.BlockedByPids.Where(p => p != parentPid && p != pid).Distinct().Order().ToList(),
            };
        }

        foreach (var (pid, kids) in childrenOf)
        {
            lists[pid].AddRange(kids.Select(k => nodes[k]));
        }

        return rootPids.Select(pid => nodes[pid]).ToList();
    }
}
