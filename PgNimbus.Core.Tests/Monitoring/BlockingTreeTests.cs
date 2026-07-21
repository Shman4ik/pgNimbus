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

        await Assert.That(roots).HasCount(1);
        await Assert.That(roots[0].Backend.Pid).IsEqualTo(100);
        await Assert.That(roots[0].Children).HasCount(1);
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

        await Assert.That(roots).HasCount(1);
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

        await Assert.That(roots).HasCount(1);
        await Assert.That(roots[0].Children.Select(c => c.Backend.Pid)).Contains(2).And.Contains(3).And.Contains(4);
        await Assert.That(roots[0].BlockedDescendants).IsEqualTo(3);
    }

    [Test]
    public async Task WaiterBlockedByMultipleHoldersAppearsUnderEach()
    {
        // 3 is blocked by both 1 and 2 (two separate lock holders).
        var roots = BlockingTree.Build([Backend(1), Backend(2), Backend(3, 1, 2)]);

        await Assert.That(roots.Select(r => r.Backend.Pid)).Contains(1).And.Contains(2);
        foreach (var root in roots)
        {
            await Assert.That(root.Children.Single().Backend.Pid).IsEqualTo(3);
        }
    }

    [Test]
    public async Task InvisibleBlockerPromotesWaiterToRoot()
    {
        // 200 is blocked by pid 999, which isn't in the snapshot (e.g. autovacuum).
        var roots = BlockingTree.Build([Backend(200, 999)]);

        await Assert.That(roots).HasCount(1);
        await Assert.That(roots[0].Backend.Pid).IsEqualTo(200);
        await Assert.That(roots[0].Children).IsEmpty();
    }

    [Test]
    public async Task DeadlockCycleIsNeverDroppedOrInfinite()
    {
        // Transient deadlock: 1 waits on 2, 2 waits on 1. Neither has a "clean"
        // root, but the build must terminate and surface both pids (the back-edge
        // that would close the cycle is cut, so each appears exactly once).
        var roots = BlockingTree.Build([Backend(1, 2), Backend(2, 1)]);

        var pids = new List<int>();
        Collect(roots, pids);

        await Assert.That(pids.Distinct().Order()).IsEquivalentTo([1, 2]);
        await Assert.That(pids).HasCount(2); // no pid rendered twice

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
