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
/// </summary>
public sealed record BlockingTreeNode(
    BlockingBackend Backend,
    IReadOnlyList<BlockingTreeNode> Children)
{
    /// <summary>Total backends blocked somewhere below this one (whole subtree, deduped).</summary>
    public int BlockedDescendants
    {
        get
        {
            var seen = new HashSet<int>();
            Count(this, seen);
            return seen.Count;

            static void Count(BlockingTreeNode node, HashSet<int> seen)
            {
                foreach (var child in node.Children)
                {
                    seen.Add(child.Backend.Pid);
                    Count(child, seen);
                }
            }
        }
    }
}

/// <summary>
/// Turns a flat list of <see cref="BlockingBackend"/> rows into a forest of
/// blocker → blocked edges. Pure logic (Core stays Avalonia-free), unit-tested:
/// the App binds a <c>TreeView</c> to the roots. Robust to the awkward shapes a
/// live server produces — chains (A blocks B blocks C), a waiter with several
/// blockers, blockers that aren't in the snapshot (autovacuum), and even a
/// transient deadlock cycle (guarded so it never recurses forever).
/// </summary>
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
        foreach (var b in backends)
        {
            foreach (var blocker in b.BlockedByPids)
            {
                if (byPid.ContainsKey(blocker))
                {
                    (blocks.TryGetValue(blocker, out var set) ? set : blocks[blocker] = new SortedSet<int>()).Add(b.Pid);
                }
            }
        }

        // A pid is "involved" if it waits on something or blocks someone.
        bool Involved(BlockingBackend b) => b.BlockedByPids.Count > 0 || blocks.ContainsKey(b.Pid);

        // A pid has a *visible* blocker only if one of its blockers is in the snapshot.
        bool HasVisibleBlocker(BlockingBackend b) => b.BlockedByPids.Any(byPid.ContainsKey);

        var roots = new List<BlockingTreeNode>();
        var placed = new HashSet<int>();

        // Primary roots: involved backends with no visible backend blocking them.
        foreach (var b in backends.Where(b => Involved(b) && !HasVisibleBlocker(b)).OrderBy(b => b.Pid))
        {
            roots.Add(BuildNode(b.Pid, byPid, blocks, placed, new HashSet<int>()));
        }

        // Anything involved but not yet placed sits inside a cycle (deadlock) —
        // promote it to a root so it's never silently dropped.
        foreach (var b in backends.Where(Involved).OrderBy(b => b.Pid))
        {
            if (!placed.Contains(b.Pid))
            {
                roots.Add(BuildNode(b.Pid, byPid, blocks, placed, new HashSet<int>()));
            }
        }

        return roots;
    }

    private static BlockingTreeNode BuildNode(
        int pid,
        Dictionary<int, BlockingBackend> byPid,
        Dictionary<int, SortedSet<int>> blocks,
        HashSet<int> placed,
        HashSet<int> path)
    {
        placed.Add(pid);
        path.Add(pid);

        var children = new List<BlockingTreeNode>();
        if (blocks.TryGetValue(pid, out var blocked))
        {
            foreach (var childPid in blocked)
            {
                // Skip an ancestor on the current path — that edge closes a cycle.
                if (!path.Contains(childPid) && byPid.ContainsKey(childPid))
                {
                    children.Add(BuildNode(childPid, byPid, blocks, placed, new HashSet<int>(path)));
                }
            }
        }

        path.Remove(pid);
        return new BlockingTreeNode(byPid[pid], children);
    }
}
