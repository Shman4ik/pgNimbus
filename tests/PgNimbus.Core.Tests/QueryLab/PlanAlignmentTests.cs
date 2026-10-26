using System.Diagnostics;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;
using static PgNimbus.Core.Tests.QueryLab.Plans;

namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>
/// Which node is which across two plans (<see cref="PlanAlignment"/>) and the merged tree
/// built from it (<see cref="PlanDiff"/>): the moves the planner makes have to read as
/// what they are, not as a subtree deleted and inserted again.
/// </summary>
public class PlanAlignmentTests
{
    private static PlanDiffNode Find(PlanDiffResult diff, Func<PlanDiffNode, bool> match) =>
        All(diff.Roots).Single(match);

    [Test]
    public async Task The_real_index_change_reads_as_a_changed_scan_with_its_gather_gone()
    {
        var diff = PlanDiff.Build(ExplainService.Import(RealPlans.BeforeJson).Result, ExplainService.Import(RealPlans.AfterJson).Result);

        var scan = Find(diff, n => n.After?.NodeType == "Bitmap Heap Scan");
        await Assert.That(scan.Change).IsEqualTo(PlanNodeChange.Changed);
        await Assert.That(scan.Before!.NodeType).IsEqualTo("Seq Scan");
        await Assert.That(scan.Differences).Contains("Seq Scan → Bitmap Heap Scan");
        await Assert.That(scan.Differences).Contains("no longer parallel");
        await Assert.That(scan.Differences).Contains("Filter removed: (customer_id = 42)");
        await Assert.That(scan.Differences).Contains("Recheck Cond added: (customer_id = 42)");

        await Assert.That(Find(diff, n => n.After?.NodeType == "Bitmap Index Scan").Change).IsEqualTo(PlanNodeChange.Added);
        var gather = Find(diff, n => n.Before?.NodeType == "Gather");
        await Assert.That(gather.Change).IsEqualTo(PlanNodeChange.Removed);

        // The Gather was the join's inner input: it sits where it was, after the outer scan.
        var join = Find(diff, n => n.After?.NodeType == "Nested Loop");
        await Assert.That(join.Change).IsEqualTo(PlanNodeChange.Same);
        await Assert.That(join.Children.Select(c => c.Header)).IsEquivalentTo(
            ["Index Scan using customers_pkey on customers c", "Gather", "Bitmap Heap Scan on orders o"],
            CollectionOrdering.Matching);

        await Assert.That(Find(diff, n => n.After?.NodeType == "Aggregate").Change).IsEqualTo(PlanNodeChange.Same);
        await Assert.That((diff.Added, diff.Removed, diff.Moved, diff.Changed)).IsEqualTo((1, 1, 0, 1));
    }

    [Test]
    public async Task Identical_plans_have_nothing_but_same_nodes()
    {
        var plan = ExplainService.Import(RealPlans.AfterJson).Result;
        var diff = PlanDiff.Build(plan, ExplainService.Import(RealPlans.AfterJson).Result);

        await Assert.That(All(diff.Roots).All(n => n.Change == PlanNodeChange.Same && !n.Moved)).IsTrue();
        await Assert.That(diff.Roots.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_seq_scan_that_became_an_index_scan_pairs_up_by_relation()
    {
        var before = Node("Aggregate", children: [Node("Seq Scan", relation: "orders", alias: "o", details: [("Filter", "(customer_id = 42)")])]);
        var after = Node("Aggregate", children: [Node("Index Scan", relation: "orders", alias: "o", index: "orders_customer_idx", details: [("Index Cond", "(customer_id = 42)")])]);

        var diff = PlanDiff.Build(Plan(before), Plan(after));
        var scan = Find(diff, n => n.After?.NodeType == "Index Scan");

        await Assert.That(scan.Differences).IsEquivalentTo([
            "Seq Scan → Index Scan", "index: none → orders_customer_idx",
            "Filter removed: (customer_id = 42)", "Index Cond added: (customer_id = 42)",
        ]);
        await Assert.That(diff.ShapeChanged).IsFalse();
    }

    [Test]
    public async Task Swapped_join_inputs_are_marked_and_the_scans_moved()
    {
        static ExplainNode Join(string outer, string inner) => Node("Hash Join", joinType: "Inner", children:
        [
            Node("Seq Scan", relation: outer, alias: outer),
            Node("Hash", children: [Node("Seq Scan", relation: inner, alias: inner)]),
        ]);

        var diff = PlanDiff.Build(Plan(Join("orders", "customers")), Plan(Join("customers", "orders")));
        var join = Find(diff, n => n.After?.NodeType == "Hash Join");

        await Assert.That(join.InputsSwapped).IsTrue();
        await Assert.That(join.Differences).Contains("outer and inner inputs swapped");
        await Assert.That((diff.Added, diff.Removed)).IsEqualTo((0, 0));
        await Assert.That(diff.Moved).IsEqualTo(2);
        await Assert.That(Find(diff, n => n.After?.RelationName == "orders").Moved).IsTrue();
    }

    [Test]
    public async Task A_new_gather_above_a_scan_is_added_and_the_scan_becomes_parallel()
    {
        var before = Node("Sort", children: [Node("Seq Scan", relation: "t", alias: "t")]);
        var after = Node("Gather Merge", children: [Node("Sort", children: [Node("Seq Scan", relation: "t", alias: "t", parallel: true)])]);

        var diff = PlanDiff.Build(Plan(before), Plan(after));

        await Assert.That(Find(diff, n => n.After?.NodeType == "Gather Merge").Change).IsEqualTo(PlanNodeChange.Added);
        await Assert.That(Find(diff, n => n.After?.NodeType == "Sort").Change).IsEqualTo(PlanNodeChange.Same);
        await Assert.That(Find(diff, n => n.After?.NodeType == "Seq Scan").Differences).IsEquivalentTo(["now parallel"]);
        await Assert.That(diff.Roots.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_sort_the_index_made_unnecessary_is_removed()
    {
        var before = Node("Sort", details: [("Sort Key", "id")], children: [Node("Seq Scan", relation: "t", alias: "t")]);
        var after = Node("Index Scan", relation: "t", alias: "t", index: "t_pkey");

        var diff = PlanDiff.Build(Plan(before), Plan(after));

        await Assert.That(diff.Roots.Select(r => r.Change)).IsEquivalentTo([PlanNodeChange.Changed, PlanNodeChange.Removed]);
        await Assert.That(diff.Roots[1].Before!.NodeType).IsEqualTo("Sort");
    }

    [Test]
    public async Task A_self_join_pairs_each_alias_with_itself()
    {
        static ExplainNode Plan2(string innerType) => Node("Hash Join", joinType: "Inner", children:
        [
            Node("Seq Scan", relation: "orders", alias: "parent"),
            Node("Hash", children: [Node(innerType, relation: "orders", alias: "child", index: innerType == "Index Scan" ? "orders_parent_idx" : null)]),
        ]);

        var diff = PlanDiff.Build(Plan(Plan2("Seq Scan")), Plan(Plan2("Index Scan")));

        await Assert.That(Find(diff, n => n.After?.Alias == "parent").Change).IsEqualTo(PlanNodeChange.Same);
        var child = Find(diff, n => n.After?.Alias == "child");
        await Assert.That(child.Before!.Alias).IsEqualTo("child");
        await Assert.That(child.Change).IsEqualTo(PlanNodeChange.Changed);
    }

    [Test]
    public async Task A_table_written_is_never_paired_with_the_same_table_read()
    {
        var before = Node("ModifyTable", relation: "orders", alias: "orders", children: [Node("Seq Scan", relation: "orders", alias: "orders")]) with { Operation = "Update" };
        var after = Node("ModifyTable", relation: "orders", alias: "orders", children: [Node("Index Scan", relation: "orders", alias: "orders", index: "orders_pkey")]) with { Operation = "Update" };

        var diff = PlanDiff.Build(Plan(before), Plan(after));

        await Assert.That(diff.Roots[0].Change).IsEqualTo(PlanNodeChange.Same);
        await Assert.That(diff.Roots[0].Children.Single().Before!.NodeType).IsEqualTo("Seq Scan");
    }

    [Test]
    [Timeout(30_000)]
    public async Task Two_thousand_partitions_with_different_pruning_align_quickly_and_fold(CancellationToken ct)
    {
        // Before scans every partition; after, pruning dropped the first 312, and the
        // aliases renumbered (orders_1 is now partition 312), so only relations anchor.
        static ExplainNode Append(int from, int to) => Node("Append", children:
        [
            .. Enumerable.Range(from, to - from).Select(p =>
                Node("Seq Scan", relation: $"orders_p{p}", alias: $"orders_{p - from + 1}")),
        ]);

        var before = Plan(Node("Aggregate", children: [Append(0, 2000)]));
        var after = Plan(Node("Aggregate", children: [Append(312, 2000)]));

        var clock = Stopwatch.StartNew();
        var diff = PlanDiff.Build(before, after);
        clock.Stop();

        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
        await Assert.That(diff.Removed).IsEqualTo(312);
        await Assert.That(diff.Added).IsEqualTo(0);
        var append = Find(diff, n => n.After?.NodeType == "Append");
        var folded = append.Children.Single(c => c.Folded > 0);
        await Assert.That(folded.Folded).IsEqualTo(312);
        await Assert.That(folded.Header).IsEqualTo("312 scans removed");
        await Assert.That(append.Children[0]).IsSameReferenceAs(folded);
        await Assert.That(append.Children.Count).IsEqualTo(1 + 1688);
    }

    [Test]
    [Timeout(30_000)]
    public async Task Twenty_thousand_removed_siblings_are_placed_in_one_pass(CancellationToken ct)
    {
        // Placing each removed node by searching the list built so far was quadratic.
        static ExplainNode Append(int from, int to) => Node("Append", children:
        [
            .. Enumerable.Range(from, to - from).Select(p => Node("Seq Scan", relation: $"orders_p{p}", alias: $"orders_p{p}")),
        ]);

        var clock = Stopwatch.StartNew();
        var diff = PlanDiff.Build(Plan(Node("Aggregate", children: [Append(0, 20_000)])), Plan(Node("Aggregate", children: [Append(19_990, 20_000)])));
        clock.Stop();

        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(3));
        await Assert.That(diff.Removed).IsEqualTo(19_990);
        var append = Find(diff, n => n.After?.NodeType == "Append");
        await Assert.That(append.Children[0].Folded).IsEqualTo(19_990);
        await Assert.That(append.Children.Skip(1).Select(c => c.After!.RelationName!)).IsEquivalentTo(
            Enumerable.Range(19_990, 10).Select(p => $"orders_p{p}"), CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_hundred_and_twenty_level_join_aligns()
    {
        static ExplainNode Chain(int depth, string lastScan)
        {
            var node = Node("Seq Scan", relation: "t0", alias: "t0");
            for (var i = 1; i < depth; i++)
            {
                var scan = i == depth - 1 ? Node(lastScan, relation: $"t{i}", alias: $"t{i}", index: lastScan == "Index Scan" ? "idx" : null)
                    : Node("Seq Scan", relation: $"t{i}", alias: $"t{i}");
                node = Node("Nested Loop", joinType: "Inner", children: [node, scan]);
            }

            return node;
        }

        var diff = PlanDiff.Build(Plan(Chain(120, "Seq Scan")), Plan(Chain(120, "Index Scan")));

        await Assert.That(diff.Changed).IsEqualTo(1);
        await Assert.That(diff.ShapeChanged).IsFalse();
        await Assert.That(diff.Same).IsEqualTo(All(diff.Roots).Count() - 1);
    }

    [Test]
    public async Task Alignment_is_the_same_every_time()
    {
        var before = ExplainService.Import(RealPlans.BeforeJson).Result;
        var after = ExplainService.Import(RealPlans.AfterJson).Result;

        static string Shape(PlanDiffResult diff) => string.Join("|", All(diff.Roots).Select(n => $"{n.Change}:{n.Header}:{n.Moved}"));

        var first = Shape(PlanDiff.Build(before, after));
        for (var i = 0; i < 5; i++)
        {
            await Assert.That(Shape(PlanDiff.Build(before, after))).IsEqualTo(first);
        }
    }
}
