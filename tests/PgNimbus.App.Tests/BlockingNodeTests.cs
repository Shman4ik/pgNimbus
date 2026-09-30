using PgNimbus.App.ViewModels;
using PgNimbus.Core.Monitoring;

namespace PgNimbus.App.Tests;

/// <summary>
/// The Blocking tab shows each backend once (security audit 2026-09, finding 16),
/// so a waiter with several blockers names the others on its own row.
/// </summary>
public class BlockingNodeTests
{
    private static BlockingBackend Backend(int pid, params int[] blockedBy) =>
        new(pid, "app", "shop", "psql", "active", null, null, 1.0, "UPDATE t SET x = 1", blockedBy, "t", "RowExclusiveLock");

    [Test]
    public async Task A_waiter_with_several_blockers_names_the_others()
    {
        var roots = BlockingTree.Build([Backend(1), Backend(2), Backend(3, 1, 2), Backend(4, 999)]);
        var nodes = roots.Select(r => new BlockingNode(r)).ToList();

        var waiter = nodes.Single(n => n.Pid == 1).Children.Single();
        await Assert.That(waiter.Pid).IsEqualTo(3);
        await Assert.That(waiter.HasOtherBlockers).IsTrue();
        await Assert.That(waiter.OtherBlockersLabel).IsEqualTo("also blocked by 2");

        // A root blocked by something outside the snapshot says so without "also".
        await Assert.That(nodes.Single(n => n.Pid == 4).OtherBlockersLabel).IsEqualTo("blocked by 999");

        // The ordinary holder names nothing.
        await Assert.That(nodes.Single(n => n.Pid == 1).HasOtherBlockers).IsFalse();
    }
}
