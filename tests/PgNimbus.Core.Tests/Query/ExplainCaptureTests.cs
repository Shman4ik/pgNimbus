using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// What a plan carries beyond its tree, and whether a figure was measured at all
/// (<see cref="ExplainCapture"/>), read the same from FORMAT JSON and FORMAT TEXT. A
/// plan comparison stands on this: "BUFFERS on, nothing read" and "BUFFERS off" have
/// to stay two different things, and statement-level lines must not land on the last
/// node the text parser read.
/// </summary>
public class ExplainCaptureTests
{
    private const string JsonWithEverything = """
        [
          {
            "Plan": {
              "Node Type": "Seq Scan",
              "Relation Name": "orders",
              "Alias": "orders",
              "Startup Cost": 0.01,
              "Total Cost": 35.51,
              "Plan Rows": 850,
              "Plan Width": 4,
              "Actual Startup Time": 0.010,
              "Actual Total Time": 0.050,
              "Actual Rows": 12,
              "Actual Loops": 1,
              "Shared Hit Blocks": 0,
              "Shared Read Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0,
              "Plans": [
                {
                  "Node Type": "Result",
                  "Parent Relationship": "InitPlan",
                  "Subplan Name": "InitPlan 1",
                  "Startup Cost": 0.00,
                  "Total Cost": 0.01,
                  "Plan Rows": 1,
                  "Plan Width": 4,
                  "Actual Startup Time": 0.001,
                  "Actual Total Time": 0.001,
                  "Actual Rows": 1,
                  "Actual Loops": 1
                }
              ]
            },
            "Settings": {
              "work_mem": "64MB",
              "random_page_cost": "1.1"
            },
            "Planning": {
              "Shared Hit Blocks": 9,
              "Shared Read Blocks": 0
            },
            "Planning Time": 0.050,
            "Triggers": [
              { "Trigger Name": "audit", "Relation": "orders", "Time": 1.000, "Calls": 2 },
              { "Trigger Name": "notify", "Relation": "orders", "Time": 0.250, "Calls": 2 }
            ],
            "JIT": {
              "Functions": 2,
              "Options": { "Inlining": false, "Optimization": false },
              "Timing": { "Generation": 0.100, "Inlining": 0.000, "Total": 5.446 }
            },
            "Execution Time": 7.100
          }
        ]
        """;

    private const string JsonWithoutBuffersOrSettings = """
        [
          {
            "Plan": {
              "Node Type": "Seq Scan",
              "Relation Name": "orders",
              "Alias": "orders",
              "Startup Cost": 0.00,
              "Total Cost": 35.50,
              "Plan Rows": 2550,
              "Plan Width": 4
            },
            "Planning Time": 0.040
          }
        ]
        """;

    private const string JsonWithDefaultSettings = """
        [ { "Plan": { "Node Type": "Result", "Total Cost": 0.01, "Plan Rows": 1, "Plan Width": 4 }, "Settings": {} } ]
        """;

    // What `EXPLAIN (ANALYZE, BUFFERS, SETTINGS)` prints for a statement that fires
    // triggers and JIT-compiles, the statement-level lines in the order EXPLAIN writes them.
    private const string TextWithEverything =
        "Hash Join  (cost=1.09..2.21 rows=3 width=8) (actual time=0.030..0.033 rows=3 loops=1)\n" +
        "  Hash Cond: (o.customer_id = c.id)\n" +
        "  Buffers: shared hit=2 read=7 dirtied=1 written=3, local hit=4, temp read=5 written=6\n" +
        "  ->  Seq Scan on orders o  (cost=0.00..1.06 rows=6 width=8) (actual time=0.008..0.009 rows=6 loops=1)\n" +
        "        Buffers: shared hit=1\n" +
        "  ->  Hash  (cost=1.05..1.05 rows=3 width=4) (actual time=0.012..0.012 rows=3 loops=1)\n" +
        "        Buckets: 4096 (originally 1024)  Batches: 2 (originally 1)  Memory Usage: 9kB\n" +
        "        ->  Bitmap Heap Scan on customers c  (cost=0.00..1.05 rows=3 width=4) (actual time=0.005..0.006 rows=3 loops=1)\n" +
        "              Recheck Cond: (id > (InitPlan 1).col1)\n" +
        "              Heap Blocks: exact=10 lossy=5\n" +
        "              InitPlan 1\n" +
        "                ->  Result  (cost=0.00..0.01 rows=1 width=4) (actual time=0.001..0.001 rows=1 loops=1)\n" +
        "              ->  Bitmap Index Scan on customers_pkey  (cost=0.00..0.01 rows=3 width=0) (actual time=0.001..0.001 rows=3 loops=1)\n" +
        "                    Index Cond: (id > (InitPlan 1).col1)\n" +
        "Settings: work_mem = '64MB', search_path = 'app, public', random_page_cost = '1.1'\n" +
        "Query Identifier: 1234567890\n" +
        "Planning:\n" +
        "  Buffers: shared hit=9 read=1\n" +
        "Planning Time: 0.079 ms\n" +
        "Trigger orders_audit on orders: time=1.000 calls=3\n" +
        "Trigger for constraint orders_customer_fk on orders: time=0.250 calls=3\n" +
        "JIT:\n" +
        "  Functions: 4\n" +
        "  Options: Inlining false, Optimization false, Expressions true, Deforming true\n" +
        "  Timing: Generation 0.567 ms, Inlining 0.000 ms, Optimization 0.312 ms, Emission 4.567 ms, Total 5.446 ms\n" +
        "Execution Time: 12.734 ms";

    [Test]
    public async Task Json_counters_that_are_all_zero_still_say_BUFFERS_was_on()
    {
        var result = ExplainService.Parse(JsonWithEverything);

        await Assert.That(result.Capture.Format).IsEqualTo(PlanFormat.Json);
        await Assert.That(result.Capture.Buffers).IsTrue();
        // The zeros stay out of the node's details, as before.
        await Assert.That(result.Root.Details.Any(d => ExplainService.IsBufferCounter(d.Key))).IsFalse();
    }

    [Test]
    public async Task Json_without_buffer_counters_says_BUFFERS_was_off()
    {
        var result = ExplainService.Parse(JsonWithoutBuffersOrSettings);

        await Assert.That(result.Capture.Buffers).IsFalse();
        await Assert.That(result.Capture.Settings).IsNull();
    }

    [Test]
    public async Task Json_settings_are_read_and_an_empty_object_means_all_defaults()
    {
        var withSettings = ExplainService.Parse(JsonWithEverything);
        var defaults = ExplainService.Parse(JsonWithDefaultSettings);

        await Assert.That(withSettings.Capture.Settings!.Select(s => $"{s.Key}={s.Value}"))
            .IsEquivalentTo(["work_mem=64MB", "random_page_cost=1.1"]);
        await Assert.That(defaults.Capture.Settings).IsNotNull();
        await Assert.That(defaults.Capture.Settings!).IsEmpty();
    }

    [Test]
    public async Task Json_statement_figures_are_read()
    {
        var result = ExplainService.Parse(JsonWithEverything);

        await Assert.That(result.Capture.PlanningBuffers.Select(d => $"{d.Key}={d.Value}"))
            .IsEquivalentTo(["Shared Hit Blocks=9"]);
        await Assert.That(result.Capture.JitMs).IsEqualTo(5.446);
        await Assert.That(result.Capture.TriggerMs).IsEqualTo(1.25);
        await Assert.That(result.Root.Children[0].ParentRelationship).IsEqualTo("InitPlan");
        await Assert.That(result.Root.ParentRelationship).IsNull();
    }

    [Test]
    public async Task Text_buffer_and_hash_lines_split_into_the_json_keys()
    {
        var result = ExplainService.Import(TextWithEverything).Result;
        var hash = result.Root.Children[1];
        var heap = hash.Children[0];

        await Assert.That(result.Root.Details.Select(d => $"{d.Key}={d.Value}")).IsEquivalentTo([
            "Hash Cond=(o.customer_id = c.id)",
            "Shared Hit Blocks=2", "Shared Read Blocks=7", "Shared Dirtied Blocks=1", "Shared Written Blocks=3",
            "Local Hit Blocks=4", "Temp Read Blocks=5", "Temp Written Blocks=6",
        ]);
        await Assert.That(hash.Details.Select(d => $"{d.Key}={d.Value}")).IsEquivalentTo([
            "Hash Buckets=4096", "Original Hash Buckets=1024", "Hash Batches=2", "Original Hash Batches=1",
            "Peak Memory Usage=9",
        ]);
        await Assert.That(heap.Details.Select(d => $"{d.Key}={d.Value}")).IsEquivalentTo([
            "Recheck Cond=(id > (InitPlan 1).col1)", "Exact Heap Blocks=10", "Lossy Heap Blocks=5",
        ]);
    }

    [Test]
    public async Task Text_plans_now_raise_the_hash_spill_and_lossy_bitmap_warnings()
    {
        var warnings = PlanAnalyzer.Analyze(ExplainService.Import(TextWithEverything).Result);

        await Assert.That(warnings.Any(w => w.Title == "Hash spilled to disk")).IsTrue();
        await Assert.That(warnings.Any(w => w.Title == "Bitmap scan went lossy")).IsTrue();
    }

    [Test]
    public async Task Text_statement_lines_are_the_statements_not_the_last_nodes()
    {
        var result = ExplainService.Import(TextWithEverything).Result;
        var lastNode = result.Root.Children[1].Children[0].Children[1];

        await Assert.That(lastNode.NodeType).IsEqualTo("Bitmap Index Scan");
        await Assert.That(lastNode.Details.Select(d => d.Key)).IsEquivalentTo(["Index Cond"]);
        await Assert.That(result.Capture.Format).IsEqualTo(PlanFormat.Text);
        await Assert.That(result.Capture.Buffers).IsTrue();
        // A value may hold the ", " that separates settings.
        await Assert.That(result.Capture.Settings!.Select(s => $"{s.Key}={s.Value}"))
            .IsEquivalentTo(["work_mem=64MB", "search_path=app, public", "random_page_cost=1.1"]);
        await Assert.That(result.Capture.PlanningBuffers.Select(d => $"{d.Key}={d.Value}"))
            .IsEquivalentTo(["Shared Hit Blocks=9", "Shared Read Blocks=1"]);
        await Assert.That(result.Capture.TriggerMs).IsEqualTo(1.25);
        await Assert.That(result.Capture.JitMs).IsEqualTo(5.446);
        await Assert.That(result.PlanningTimeMs).IsEqualTo(0.079);
        await Assert.That(result.ExecutionTimeMs).IsEqualTo(12.734);
    }

    [Test]
    public async Task Text_subplan_headers_name_the_child_that_follows()
    {
        var heap = ExplainService.Import(TextWithEverything).Result.Root.Children[1].Children[0];

        await Assert.That(heap.Children.Count).IsEqualTo(2);
        await Assert.That(heap.Children[0].NodeType).IsEqualTo("Result");
        await Assert.That(heap.Children[0].SubplanName).IsEqualTo("InitPlan 1");
        await Assert.That(heap.Children[0].ParentRelationship).IsEqualTo("InitPlan");
        await Assert.That(heap.Children[1].SubplanName).IsNull();
        await Assert.That(heap.Children[1].ParentRelationship).IsNull();
    }

    [Test]
    public async Task Text_without_a_buffers_line_cannot_tell_whether_BUFFERS_was_on()
    {
        var result = ExplainService.Import("Seq Scan on t  (cost=0.00..35.50 rows=2550 width=4)").Result;

        await Assert.That(result.Capture.Buffers).IsNull();
        await Assert.That(result.Capture.Settings).IsNull();
        await Assert.That(result.Capture.JitMs).IsNull();
        await Assert.That(result.Capture.TriggerMs).IsNull();
    }

    [Test]
    public async Task Text_sort_method_splits_its_space_off()
    {
        var root = ExplainService.Import(
            "Sort  (cost=10.00..11.00 rows=100 width=4) (actual time=1.000..1.200 rows=100 loops=1)\n" +
            "  Sort Key: id\n" +
            "  Sort Method: external merge  Disk: 2400kB\n" +
            "  ->  Seq Scan on t  (cost=0.00..5.00 rows=100 width=4) (actual time=0.010..0.100 rows=100 loops=1)").Result.Root;

        await Assert.That(root.Details.Select(d => $"{d.Key}={d.Value}")).IsEquivalentTo([
            "Sort Key=id", "Sort Method=external merge", "Sort Space Used=2400", "Sort Space Type=Disk",
        ]);
    }

    [Test]
    public async Task Text_hash_aggregate_batches_use_the_hashagg_keys()
    {
        var root = ExplainService.Import(
            "HashAggregate  (cost=10.00..11.00 rows=100 width=12) (actual time=1.000..1.200 rows=100 loops=1)\n" +
            "  Group Key: customer_id\n" +
            "  Planned Partitions: 4  Batches: 5  Memory Usage: 4145kB  Disk Usage: 7680kB\n" +
            "  ->  Seq Scan on t  (cost=0.00..5.00 rows=100 width=4) (actual time=0.010..0.100 rows=100 loops=1)").Result.Root;

        await Assert.That(root.Details.Select(d => $"{d.Key}={d.Value}")).IsEquivalentTo([
            "Group Key=customer_id", "Planned Partitions=4", "HashAgg Batches=5", "Peak Memory Usage=4145",
            "Disk Usage=7680",
        ]);
    }
}
