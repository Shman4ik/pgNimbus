using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Heuristic tests for <see cref="PlanAnalyzer"/> over captured
/// `EXPLAIN (ANALYZE, FORMAT JSON, BUFFERS)` shapes — one case per rule, plus a
/// clean plan that must stay silent and the row-estimate boundary/direction.
/// </summary>
public class PlanAnalyzerTests
{
    // Seq scan reading 100k rows and filtering all but 5 away → both "discards most rows"
    // and a huge under-estimate (planner guessed 5, hit 100000).
    private const string SeqScanFilterJson = """
        [
          {
            "Plan": {
              "Node Type": "Seq Scan",
              "Parallel Aware": false,
              "Relation Name": "events",
              "Alias": "events",
              "Startup Cost": 0.00,
              "Total Cost": 1834.00,
              "Plan Rows": 5,
              "Plan Width": 8,
              "Actual Startup Time": 0.020,
              "Actual Total Time": 25.000,
              "Actual Rows": 5.00,
              "Actual Loops": 1,
              "Filter": "(status = 'x')",
              "Rows Removed by Filter": 99995
            },
            "Planning Time": 0.100,
            "Execution Time": 25.500
          }
        ]
        """;

    // Planner expected 5 output rows, the node actually returned 100000 → a 20000× under-estimate.
    private const string MisestimateJson = """
        [
          {
            "Plan": {
              "Node Type": "Seq Scan",
              "Parallel Aware": false,
              "Relation Name": "orders",
              "Alias": "orders",
              "Startup Cost": 0.00,
              "Total Cost": 1834.00,
              "Plan Rows": 5,
              "Plan Width": 8,
              "Actual Startup Time": 0.020,
              "Actual Total Time": 25.000,
              "Actual Rows": 100000.00,
              "Actual Loops": 1
            },
            "Planning Time": 0.100,
            "Execution Time": 25.500
          }
        ]
        """;

    private const string DiskSortJson = """
        [
          {
            "Plan": {
              "Node Type": "Sort",
              "Parallel Aware": false,
              "Startup Cost": 10.00,
              "Total Cost": 20.00,
              "Plan Rows": 100000,
              "Plan Width": 8,
              "Actual Startup Time": 50.0,
              "Actual Total Time": 60.0,
              "Actual Rows": 100000,
              "Actual Loops": 1,
              "Sort Key": ["n"],
              "Sort Method": "external merge",
              "Sort Space Used": 2048,
              "Sort Space Type": "Disk"
            },
            "Planning Time": 0.1,
            "Execution Time": 70.0
          }
        ]
        """;

    private const string LossyBitmapJson = """
        [
          {
            "Plan": {
              "Node Type": "Bitmap Heap Scan",
              "Parallel Aware": false,
              "Relation Name": "big",
              "Alias": "big",
              "Startup Cost": 10.00,
              "Total Cost": 20.00,
              "Plan Rows": 50000,
              "Plan Width": 8,
              "Actual Startup Time": 5.0,
              "Actual Total Time": 15.0,
              "Actual Rows": 50000,
              "Actual Loops": 1,
              "Exact Heap Blocks": 100,
              "Lossy Heap Blocks": 4200
            },
            "Planning Time": 0.1,
            "Execution Time": 20.0
          }
        ]
        """;

    // A small, well-estimated index scan — the analyzer must stay silent.
    private const string CleanPlanJson = """
        [
          {
            "Plan": {
              "Node Type": "Index Scan",
              "Parallel Aware": false,
              "Scan Direction": "Forward",
              "Index Name": "t_pkey",
              "Relation Name": "t",
              "Alias": "t",
              "Startup Cost": 0.00,
              "Total Cost": 8.30,
              "Plan Rows": 10,
              "Plan Width": 8,
              "Actual Startup Time": 0.010,
              "Actual Total Time": 0.020,
              "Actual Rows": 10.00,
              "Actual Loops": 1
            },
            "Planning Time": 0.1,
            "Execution Time": 0.05
          }
        ]
        """;

    [Test]
    public async Task SeqScanDiscardingMostRowsIsFlagged()
    {
        var warnings = PlanAnalyzer.Analyze(ExplainService.Parse(SeqScanFilterJson));

        await Assert.That(warnings.Any(w => w.Title == "Sequential scan discards most rows")).IsTrue();
        await Assert.That(warnings.Any(w => w.Relation == "events")).IsTrue();
    }

    [Test]
    public async Task LargeRowMisestimateIsFlaggedAsCritical()
    {
        var warnings = PlanAnalyzer.Analyze(ExplainService.Parse(MisestimateJson));
        var estimate = warnings.SingleOrDefault(w => w.Title.StartsWith("Row estimate off by"));

        await Assert.That(estimate).IsNotNull();
        await Assert.That(estimate!.Severity).IsEqualTo(PlanWarningSeverity.Critical);
        // Planner guessed 5, saw 100000 → an under-estimate.
        await Assert.That(estimate.Detail).Contains("underestimated");
    }

    [Test]
    public async Task DiskSortIsFlaggedWithWorkMemHint()
    {
        var warnings = PlanAnalyzer.Analyze(ExplainService.Parse(DiskSortJson));
        var sort = warnings.SingleOrDefault(w => w.Title == "Sort spilled to disk");

        await Assert.That(sort).IsNotNull();
        await Assert.That(sort!.Detail).Contains("work_mem");
    }

    [Test]
    public async Task LossyBitmapIsFlagged()
    {
        var warnings = PlanAnalyzer.Analyze(ExplainService.Parse(LossyBitmapJson));

        await Assert.That(warnings.Any(w => w.Title == "Bitmap scan went lossy")).IsTrue();
    }

    [Test]
    public async Task CleanPlanProducesNoWarnings()
    {
        var warnings = PlanAnalyzer.Analyze(ExplainService.Parse(CleanPlanJson));

        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task SmallCountsBelowThresholdAreNotFlagged()
    {
        // Estimated 1, actual 50 — a 50× ratio, but both below the 100-row floor, so it's noise.
        var json = CleanPlanJson
            .Replace("\"Plan Rows\": 10", "\"Plan Rows\": 1")
            .Replace("\"Actual Rows\": 10.00", "\"Actual Rows\": 50.00");
        var warnings = PlanAnalyzer.Analyze(ExplainService.Parse(json));

        await Assert.That(warnings).IsEmpty();
    }

    // A plan node with ANALYZE counts; children nest through "Plans".
    private static string Node(string type, long planRows, double actualRows, string extra = "", params string[] children) =>
        $$"""
        { "Node Type": "{{type}}", "Relation Name": "t", "Startup Cost": 0, "Total Cost": 1,
          "Plan Rows": {{planRows}}, "Plan Width": 8, "Actual Startup Time": 0.01, "Actual Total Time": 1,
          "Actual Rows": {{actualRows}}, "Actual Loops": 1{{extra}}
          {{(children.Length == 0 ? "" : $", \"Plans\": [{string.Join(",", children)}]")}} }
        """;

    private static IReadOnlyList<PlanWarning> Analyze(string root) =>
        PlanAnalyzer.Analyze(ExplainService.Parse($$"""[ { "Plan": {{root}}, "Execution Time": 1 } ]"""));

    private static IEnumerable<PlanWarning> Estimates(IReadOnlyList<PlanWarning> warnings) =>
        warnings.Where(w => w.Title.StartsWith("Row estimate off by"));

    [Test]
    public async Task BrowsePageUnderLimitIsNotAnOverestimate()
    {
        // SELECT * FROM t LIMIT 100 on a million-row table: the scan stops after 100 rows.
        var warnings = Analyze(Node("Limit", 100, 100, "", Node("Seq Scan", 1_000_000, 100)));

        await Assert.That(Estimates(warnings)).IsEmpty();
    }

    [Test]
    public async Task EveryStreamingNodeUnderLimitIsExempt()
    {
        // The Limit's early stop reaches through a join to both of its inputs.
        var warnings = Analyze(Node("Limit", 100, 100, "",
            Node("Nested Loop", 500_000, 100, "",
                Node("Seq Scan", 1_000_000, 100),
                Node("Index Scan", 5_000, 1))));

        await Assert.That(Estimates(warnings)).IsEmpty();
    }

    [Test]
    public async Task UnderestimateUnderLimitIsStillFlagged()
    {
        // Stopping early can only lower the count, so more rows than planned is a real misestimate.
        var warnings = Analyze(Node("Limit", 100, 100, "", Node("Seq Scan", 1, 5_000)));

        await Assert.That(Estimates(warnings).Count()).IsEqualTo(1);
        await Assert.That(Estimates(warnings).Single().Detail).Contains("underestimated");
    }

    [Test]
    public async Task SortReadsItsWholeInputSoItsInputIsJudged()
    {
        // ORDER BY … LIMIT: the Sort's own output is cut short, but it read every row first,
        // so the scan under it ran to completion and its overestimate is genuine.
        var warnings = Analyze(Node("Limit", 100, 100, "",
            Node("Sort", 1_000_000, 100, "",
                Node("Seq Scan", 1_000_000, 2_000))));

        var flagged = Estimates(warnings).Select(w => w.NodeType).ToList();
        await Assert.That(flagged).IsEquivalentTo(new[] { "Seq Scan" });
    }

    [Test]
    public async Task HashIsBuiltInFullUnderLimit()
    {
        // The join stops early, but the hash side was built from every row before it began.
        var warnings = Analyze(Node("Limit", 100, 100, "",
            Node("Hash Join", 1_000_000, 100, "",
                Node("Seq Scan", 1_000_000, 100),
                Node("Hash", 50_000, 200, "",
                    Node("Seq Scan", 50_000, 200)))));

        var flagged = Estimates(warnings).Select(w => w.NodeType).ToList();
        await Assert.That(flagged).IsEquivalentTo(new[] { "Hash", "Seq Scan" });
    }

    [Test]
    public async Task HashedAggregateReadsItsWholeInput()
    {
        var warnings = Analyze(Node("Limit", 100, 100, "",
            Node("Aggregate", 50_000, 100, ", \"Strategy\": \"Hashed\"",
                Node("Seq Scan", 1_000_000, 3_000))));

        var flagged = Estimates(warnings).Select(w => w.NodeType).ToList();
        await Assert.That(flagged).IsEquivalentTo(new[] { "Seq Scan" });
    }

    [Test]
    public async Task SortedAggregateStreamsSoItsInputIsExempt()
    {
        var warnings = Analyze(Node("Limit", 100, 100, "",
            Node("Aggregate", 50_000, 100, ", \"Strategy\": \"Sorted\"",
                Node("Index Scan", 1_000_000, 3_000))));

        await Assert.That(Estimates(warnings)).IsEmpty();
    }

    [Test]
    public async Task LimitThatRanItsInputDryDoesNotExemptIt()
    {
        // LIMIT 100 over a table the planner thinks holds 5000 rows but that holds 5:
        // the scan was never stopped, so its overestimate is real.
        var warnings = Analyze(Node("Limit", 100, 5, "", Node("Seq Scan", 5_000, 5)));

        var flagged = Estimates(warnings).Select(w => w.NodeType).ToList();
        await Assert.That(flagged).Contains("Seq Scan");
    }

    [Test]
    public async Task TextPlanUnderLimitIsNotAnOverestimate()
    {
        const string text = """
            Limit  (cost=0.00..1.54 rows=100 width=8) (actual time=0.010..0.050 rows=100 loops=1)
              ->  Seq Scan on t  (cost=0.00..15406.00 rows=1000000 width=8) (actual time=0.009..0.030 rows=100 loops=1)
            """;
        var warnings = PlanAnalyzer.Analyze(ExplainService.Import(text).Result);

        await Assert.That(Estimates(warnings)).IsEmpty();
    }
}
