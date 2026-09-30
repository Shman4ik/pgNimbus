using PgNimbus.Core.Monitoring;

namespace PgNimbus.Core.Tests.Monitoring;

public class BlockingTreeTests
{
    private static BlockingBackend Backend(int pid, params int[] blockedBy) =>
        new(pid, "alice", "app", "psql", "active", blockedBy.Length > 0 ? "Lock" : null,
            blockedBy.Length > 0 ? "relation" : null, 1.0, "UPDATE t SET x = 1", blockedBy, "t", "RowExclusiveLock");

    [Test]
    public async Task EmptyInputHasNoRoots()
    {
        await Assert.That(BlockingTree.Build([])).IsEmpty();
    }

    [Test]
    public async Task SingleBlockerWithOneWaiter()
    {
        // 100 holds the lock, 200 waits on 100.
        var roots = BlockingTree.Build([Backend(100), Backend(200, 100)]);

        await Assert.That(roots).Count().IsEqualTo(1);
        await Assert.That(roots[0].Backend.Pid).IsEqualTo(100);
        await Assert.That(roots[0].Children).Count().IsEqualTo(1);
        await Assert.That(roots[0].Children[0].Backend.Pid).IsEqualTo(200);
        await Assert.That(roots[0].BlockedDescendants).IsEqualTo(1);
    }

    [Test]
    public async Task WaiterIsNeverAlsoARoot()
    {
        var roots = BlockingTree.Build([Backend(100), Backend(200, 100)]);

        // 200 is blocked by 100, so it appears only under 100 — not at the top.
        await Assert.That(roots.Select(r => r.Backend.Pid)).DoesNotContain(200);
    }

    [Test]
    public async Task ChainNestsInOrder()
    {
        // 1 blocks 2 blocks 3.
        var roots = BlockingTree.Build([Backend(1), Backend(2, 1), Backend(3, 2)]);

        await Assert.That(roots).Count().IsEqualTo(1);
        await Assert.That(roots[0].Backend.Pid).IsEqualTo(1);
        await Assert.That(roots[0].BlockedDescendants).IsEqualTo(2);

        var mid = roots[0].Children.Single();
        await Assert.That(mid.Backend.Pid).IsEqualTo(2);
        await Assert.That(mid.Children.Single().Backend.Pid).IsEqualTo(3);
    }

    [Test]
    public async Task OneBlockerFansOutToManyWaiters()
    {
        var roots = BlockingTree.Build([Backend(1), Backend(2, 1), Backend(3, 1), Backend(4, 1)]);

        await Assert.That(roots).Count().IsEqualTo(1);
        await Assert.That(roots[0].Children.Select(c => c.Backend.Pid)).Contains(2).And.Contains(3).And.Contains(4);
        await Assert.That(roots[0].BlockedDescendants).IsEqualTo(3);
    }

    [Test]
    public async Task WaiterBlockedByMultipleHoldersAppearsOnceAndNamesTheOthers()
    {
        // 3 is blocked by both 1 and 2 (two separate lock holders). It used to be
        // repeated under each blocker, subtree and all; now it sits under the first
        // (lowest pid) and names the other, so each backend appears exactly once.
        var roots = BlockingTree.Build([Backend(1), Backend(2), Backend(3, 1, 2)]);

        await Assert.That(roots.Select(r => r.Backend.Pid)).IsEquivalentTo([1, 2]);
        await Assert.That(roots[0].Children.Single().Backend.Pid).IsEqualTo(3);
        await Assert.That(roots[0].Children.Single().AlsoBlockedBy).IsEquivalentTo([2]);
        await Assert.That(roots[1].Children).IsEmpty();
        await Assert.That(AllPids(roots)).Count().IsEqualTo(3);
    }

    [Test]
    [Timeout(10_000)]
    public async Task A_complete_wait_graph_of_thirty_builds_one_node_per_backend(CancellationToken ct)
    {
        // N sessions queued on one hot row: pg_blocking_pids reports every earlier
        // waiter as a (soft) blocker of every later one, a complete DAG. Repeating
        // each waiter under every blocker made ~2^(N-3) nodes: 134M for 30, built on
        // the UI thread every 2 s (security audit 2026-09, finding 16).
        const int waiters = 30;
        var backends = new List<BlockingBackend> { Backend(1) };
        for (var pid = 2; pid <= waiters + 1; pid++)
        {
            backends.Add(Backend(pid, [.. Enumerable.Range(1, pid - 1)]));
        }

        var roots = BlockingTree.Build(backends);

        var pids = AllPids(roots);
        await Assert.That(pids).Count().IsEqualTo(waiters + 1);
        await Assert.That(pids.Distinct().Count()).IsEqualTo(waiters + 1);
        await Assert.That(roots).Count().IsEqualTo(1);
        await Assert.That(roots[0].BlockedDescendants).IsEqualTo(waiters);
        // Breadth-first from the holder: every waiter is a direct child of pid 1, the
        // first blocker it has, and names the rest.
        await Assert.That(roots[0].Children.Select(c => c.Backend.Pid)).IsEquivalentTo(Enumerable.Range(2, waiters));
        await Assert.That(roots[0].Children[^1].AlsoBlockedBy).IsEquivalentTo(Enumerable.Range(2, waiters - 1));
    }

    [Test]
    public async Task The_shape_is_the_same_whatever_order_the_rows_arrive_in()
    {
        BlockingBackend[] rows = [Backend(10), Backend(20, 10), Backend(30, 20, 10), Backend(40, 30), Backend(50, 40, 20)];

        var forward = Describe(BlockingTree.Build(rows));
        var backward = Describe(BlockingTree.Build([.. rows.Reverse()]));

        await Assert.That(backward).IsEqualTo(forward);
    }

    private static List<int> AllPids(IReadOnlyList<BlockingTreeNode> nodes)
    {
        var pids = new List<int>();
        var stack = new Stack<BlockingTreeNode>(nodes);
        while (stack.TryPop(out var node))
        {
            pids.Add(node.Backend.Pid);
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        return pids;
    }

    private static string Describe(IReadOnlyList<BlockingTreeNode> nodes) =>
        string.Join(",", nodes.Select(n => $"{n.Backend.Pid}[{string.Join('+', n.AlsoBlockedBy)}]({Describe(n.Children)})"));

    [Test]
    public async Task InvisibleBlockerPromotesWaiterToRoot()
    {
        // 200 is blocked by pid 999, which isn't in the snapshot (e.g. autovacuum).
        var roots = BlockingTree.Build([Backend(200, 999)]);

        await Assert.That(roots).Count().IsEqualTo(1);
        await Assert.That(roots[0].Backend.Pid).IsEqualTo(200);
        await Assert.That(roots[0].Children).IsEmpty();
        await Assert.That(roots[0].AlsoBlockedBy).IsEquivalentTo([999]);
    }

    [Test]
    public async Task DeadlockCycleIsNeverDroppedOrInfinite()
    {
        // Transient deadlock: 1 waits on 2, 2 waits on 1. Neither has a "clean"
        // root, but the build must terminate and surface both pids (the back-edge
        // that would close the cycle is cut, so each appears exactly once, and the
        // promoted root names the blocker the cut edge came from).
        var roots = BlockingTree.Build([Backend(1, 2), Backend(2, 1)]);

        var pids = new List<int>();
        Collect(roots, pids);

        await Assert.That(pids.Distinct().Order()).IsEquivalentTo([1, 2]);
        await Assert.That(pids).Count().IsEqualTo(2); // no pid rendered twice
        await Assert.That(roots.Single().Backend.Pid).IsEqualTo(1);
        await Assert.That(roots.Single().AlsoBlockedBy).IsEquivalentTo([2]);

        static void Collect(IReadOnlyList<BlockingTreeNode> nodes, List<int> acc)
        {
            foreach (var n in nodes)
            {
                acc.Add(n.Backend.Pid);
                Collect(n.Children, acc);
            }
        }
    }
}
