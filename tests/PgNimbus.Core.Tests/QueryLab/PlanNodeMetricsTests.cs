using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;
using static PgNimbus.Core.Tests.QueryLab.Plans;

namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>
/// What each node's figures mean once loops, parallel workers, InitPlans and missing
/// measurements are taken into account (<see cref="PlanMetrics"/>).
/// </summary>
public class PlanNodeMetricsTests
{
    [Test]
    public async Task Time_and_rows_count_every_loop()
    {
        var inner = Node("Index Scan", relation: "orders", index: "orders_pkey", rows: 1).Ran(ms: 0.01, rows: 2.5, loops: 40);
        var root = Node("Nested Loop", children: [Node("Seq Scan", relation: "customers").Ran(1, 40), inner]).Ran(2, 100);
        var metrics = PlanMetrics.Compute(Plan(root, capture: Measured()));

        await Assert.That(metrics[inner].InclusiveMs!.Value).IsEqualTo(0.4).Within(1e-9);
        await Assert.That(metrics[inner].ActualRows).IsEqualTo(2.5);
        await Assert.That(metrics[inner].ActualRowsTotal).IsEqualTo(100);
        await Assert.That(metrics[inner].EstimatedRows).IsEqualTo(1);
        await Assert.That(metrics[root].SelfMs!.Value).IsEqualTo(0.6).Within(1e-9);
    }

    [Test]
    public async Task Under_a_gather_time_is_divided_by_the_leader_and_workers()
    {
        // EXPLAIN averages a parallel node's time over the leader and its two workers;
        // times loops alone would make the scan take three times the Gather above it.
        var scan = Node("Seq Scan", relation: "orders", parallel: true).Ran(ms: 8.8, rows: 20, loops: 3);
        var gather = Node("Gather", details: [("Workers Planned", "2"), ("Workers Launched", "2")], children: [scan]).Ran(13.3, 60);
        var metrics = PlanMetrics.Compute(Plan(gather, capture: Measured()));

        await Assert.That(metrics[scan].InclusiveMs!.Value).IsEqualTo(8.8).Within(1e-9);
        await Assert.That(metrics[scan].TimeApproximate).IsTrue();
        await Assert.That(metrics[gather].SelfMs!.Value).IsEqualTo(4.5).Within(1e-9);
        await Assert.That(metrics[gather].TimeApproximate).IsFalse();
        await Assert.That(metrics[gather].WorkersLaunched).IsEqualTo(2);
    }

    [Test]
    public async Task An_initplan_is_not_subtracted_from_the_node_that_reads_it()
    {
        var init = Node("Result", parent: "InitPlan").Ran(4, 1);
        var scan = Node("Seq Scan", relation: "t", children: [init]).Ran(10, 5);
        var metrics = PlanMetrics.Compute(Plan(scan, capture: Measured()));

        await Assert.That(metrics[scan].SelfMs).IsEqualTo(10);
    }

    [Test]
    public async Task A_never_executed_node_took_no_time_in_a_timed_plan()
    {
        var skipped = Node("Index Scan", relation: "t", index: "t_pkey").NeverRan();
        var root = Node("Nested Loop", children: [Node("Seq Scan", relation: "u").Ran(1, 0), skipped]).Ran(1, 0);
        var metrics = PlanMetrics.Compute(Plan(root, capture: Measured()));

        await Assert.That(metrics[skipped].NeverExecuted).IsTrue();
        await Assert.That(metrics[skipped].InclusiveMs).IsEqualTo(0);
    }

    [Test]
    public async Task A_plain_explain_has_no_actual_figures()
    {
        var scan = Node("Seq Scan", relation: "t");
        var metrics = PlanMetrics.Compute(Plan(scan, capture: Measured(buffers: true)))[scan];

        await Assert.That(metrics.Analyzed).IsFalse();
        await Assert.That(metrics.ActualRows).IsNull();
        await Assert.That(metrics.InclusiveMs).IsNull();
        // A plain EXPLAIN (BUFFERS) counts planning only: the node was never measured.
        await Assert.That(metrics.Buffers).IsNull();
    }

    [Test]
    public async Task Missing_counters_are_zeros_only_when_buffers_were_measured()
    {
        var child = Node("Seq Scan", relation: "t", details: [("Shared Hit Blocks", "40"), ("Shared Read Blocks", "2")]).Ran(1, 10);
        var root = Node("Sort", details: [("Shared Hit Blocks", "41"), ("Shared Read Blocks", "2")], children: [child]).Ran(2, 10);

        var measured = PlanMetrics.Compute(Plan(root, capture: Measured(buffers: true)));
        var off = PlanMetrics.Compute(Plan(Node("Seq Scan", relation: "t").Ran(1, 10), capture: Measured(buffers: false)));
        var unknown = PlanMetrics.Compute(Plan(Node("Seq Scan", relation: "t").Ran(1, 10), capture: Measured(buffers: null)));

        await Assert.That(measured[root].Buffers!.Value.SharedHit).IsEqualTo(41);
        await Assert.That(measured[root].SelfBuffers!.Value.SharedHit).IsEqualTo(1);
        await Assert.That(measured[root].SelfBuffers!.Value.SharedRead).IsEqualTo(0);
        await Assert.That(measured[root].Buffers!.Value.TempWritten).IsEqualTo(0);
        await Assert.That(off.Values.Single().Buffers).IsNull();
        await Assert.That(unknown.Values.Single().Buffers).IsNull();
    }

    [Test]
    public async Task Spills_name_why_the_node_went_to_disk()
    {
        var sort = Node("Sort", details: [("Sort Method", "external merge"), ("Sort Space Used", "2400")]);
        var hash = Node("Hash", details: [("Hash Batches", "4")]);
        var agg = Node("Aggregate", strategy: "Hashed", details: [("HashAgg Batches", "5"), ("Disk Usage", "7680")]);
        // A sort that spilled in a worker while the leader's line says quicksort.
        var worker = Node("Sort", details: [("Sort Method", "quicksort"), ("Temp Written Blocks", "120")]);
        var root = Node("Append", children: [sort.Ran(1, 1), hash.Ran(1, 1), agg.Ran(1, 1), worker.Ran(1, 1)]).Ran(5, 4);
        var metrics = PlanMetrics.Compute(Plan(root, capture: Measured()));

        await Assert.That(metrics[root.Children[0]].Spill).IsEqualTo("sort on disk (2400 kB)");
        await Assert.That(metrics[root.Children[1]].Spill).IsEqualTo("hash in 4 batches");
        await Assert.That(metrics[root.Children[2]].Spill).IsEqualTo("hash aggregate in 5 batches");
        await Assert.That(metrics[root.Children[3]].Spill).IsEqualTo("wrote 120 temp blocks");
        await Assert.That(metrics[root].Spill).IsNull();
    }

    [Test]
    public async Task The_real_parallel_plan_reads_as_expected()
    {
        var result = ExplainService.Import(RealPlans.BeforeJson).Result;
        var metrics = PlanMetrics.Compute(result);
        var gather = result.Root.Children[0].Children[0].Children[1];
        var scan = gather.Children[0];

        await Assert.That(gather.NodeType).IsEqualTo("Gather");
        await Assert.That(metrics[scan].ActualRowsTotal).IsEqualTo(metrics[gather].ActualRowsTotal);
        await Assert.That(metrics[scan].InclusiveMs!.Value).IsLessThan(metrics[gather].InclusiveMs!.Value);
        await Assert.That(metrics[result.Root].Buffers!.Value.SharedHit).IsGreaterThan(0);
    }
}
