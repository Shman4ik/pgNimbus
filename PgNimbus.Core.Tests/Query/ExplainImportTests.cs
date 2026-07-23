using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Covers the paste-a-plan import path: tolerant JSON parsing (the shapes external
/// tools produce) and the best-effort <c>FORMAT TEXT</c> parser. The text cases
/// round-trip against <see cref="ExplainTextFormatter"/> output so the two parsers
/// stay in agreement.
/// </summary>
public class ExplainImportTests
{
    private const string StandardJson = """
        [
          {
            "Plan": {
              "Node Type": "Seq Scan",
              "Relation Name": "t",
              "Alias": "t",
              "Startup Cost": 0.00,
              "Total Cost": 41.88,
              "Plan Rows": 850,
              "Plan Width": 4
            },
            "Planning Time": 0.181,
            "Execution Time": 0.021
          }
        ]
        """;

    [Test]
    public async Task ImportDetectsJsonAndFormatsDisplayText()
    {
        var imported = ExplainService.Import(StandardJson);

        await Assert.That(imported.Result.Root.NodeType).IsEqualTo("Seq Scan");
        await Assert.That(imported.Result.ExecutionTimeMs).IsEqualTo(0.021);
        // JSON imports show the canonical text layout, not the raw JSON.
        await Assert.That(imported.DisplayText).StartsWith("Seq Scan on t  (cost=0.00..41.88");
    }

    [Test]
    public async Task JsonImportKeepsRawJsonTextImportDoesNot()
    {
        var json = ExplainService.Import(StandardJson);
        await Assert.That(json.RawJson).IsNotNull();
        await Assert.That(json.RawJson!).Contains("\"Node Type\"");

        var text = ExplainService.Import("Seq Scan on t  (cost=0.00..1.05 rows=5 width=4)");
        // A text import has no JSON to copy/export.
        await Assert.That(text.RawJson).IsNull();
    }

    [Test]
    public async Task BarePlanNodeWithoutWrapperParses()
    {
        // Some tools export just the plan node (no [{ "Plan": … }] envelope).
        var bare = """
            { "Node Type": "Result", "Startup Cost": 0.00, "Total Cost": 0.01, "Plan Rows": 1, "Plan Width": 4 }
            """;

        var result = ExplainService.Parse(bare);

        await Assert.That(result.Root.NodeType).IsEqualTo("Result");
        await Assert.That(result.PlanningTimeMs).IsNull();
    }

    [Test]
    public async Task ObjectRootWithPlanParses()
    {
        var obj = """
            { "Plan": { "Node Type": "Result", "Startup Cost": 0, "Total Cost": 0.01, "Plan Rows": 1, "Plan Width": 4 }, "Execution Time": 5.5 }
            """;

        var result = ExplainService.Parse(obj);

        await Assert.That(result.Root.NodeType).IsEqualTo("Result");
        await Assert.That(result.ExecutionTimeMs).IsEqualTo(5.5);
    }

    [Test]
    public async Task BlankInputThrowsFriendlyError()
    {
        await Assert.That(() => ExplainService.Import("   ")).Throws<FormatException>();
    }

    [Test]
    public async Task InvalidJsonThrowsFormatException()
    {
        // Starts with '{' so it's routed to the JSON parser, then fails to parse.
        await Assert.That(() => ExplainService.Import("{ not json ")).Throws<FormatException>();
    }

    [Test]
    public async Task TextPlanBuildsTreeCostAndActual()
    {
        var text =
            "Sort  (cost=1.10..1.20 rows=5 width=4) (actual time=0.050..0.060 rows=5 loops=1)\n" +
            "  Sort Key: t.id\n" +
            "  Sort Method: external merge  Disk: 2000kB\n" +
            "  ->  Seq Scan on t  (cost=0.00..1.05 rows=5 width=4) (actual time=0.005..0.010 rows=5 loops=1)\n" +
            "        Filter: (id > 3)\n" +
            "        Rows Removed by Filter: 3\n" +
            "Planning Time: 0.100 ms\n" +
            "Execution Time: 0.200 ms";

        var result = ExplainPlanTextParser.Parse(text);

        await Assert.That(result.Root.NodeType).IsEqualTo("Sort");
        await Assert.That(result.Root.TotalCost).IsEqualTo(1.20);
        await Assert.That(result.Root.ActualTotalTimeMs).IsEqualTo(0.060);
        await Assert.That(result.Root.ActualRows).IsEqualTo(5.0);
        await Assert.That(result.PlanningTimeMs).IsEqualTo(0.100);
        await Assert.That(result.ExecutionTimeMs).IsEqualTo(0.200);

        await Assert.That(result.Root.Children.Count).IsEqualTo(1);
        var child = result.Root.Children[0];
        await Assert.That(child.NodeType).IsEqualTo("Seq Scan");
        await Assert.That(child.RelationName).IsEqualTo("t");
        await Assert.That(child.Details.Select(d => d.Key)).Contains("Rows Removed by Filter");
    }

    [Test]
    public async Task TextPlanRoundTripsThroughTextFormatter()
    {
        // Parse the exact text the formatter emits for the join fixture, and the
        // reconstructed tree must re-format to the same text.
        var expected =
            "Hash Left Join  (cost=1.11..2.29 rows=5 width=30)\n" +
            "  Hash Cond: (o.customer_id = c.id)\n" +
            "  ->  Seq Scan on orders o  (cost=0.00..1.05 rows=5 width=12)\n" +
            "  ->  Hash  (cost=1.05..1.05 rows=5 width=22)\n" +
            "        ->  Index Scan using customers_pkey on customers c  (cost=0.00..1.05 rows=5 width=22)\n" +
            "Planning Time: 0.120 ms";

        var result = ExplainPlanTextParser.Parse(expected);
        var reformatted = ExplainTextFormatter.Format(result).ReplaceLineEndings("\n");

        await Assert.That(reformatted).IsEqualTo(expected);
    }

    [Test]
    public async Task TextPlanAnalyzerFiresOnImportedSpill()
    {
        // The disk-spill and seq-scan-filter analyzers must work on an imported text
        // plan, proving the parsed shape feeds PlanAnalyzer the same as a live plan.
        var text =
            "Seq Scan on events  (cost=0.00..1000.00 rows=100 width=4) (actual time=0.010..50.000 rows=10 loops=1)\n" +
            "  Filter: (amount > 100)\n" +
            "  Rows Removed by Filter: 190000";

        var result = ExplainPlanTextParser.Parse(ExplainPlanTextParser.Clean(text));
        var warnings = PlanAnalyzer.Analyze(result);

        await Assert.That(warnings.Any(w => w.Title == "Sequential scan discards most rows")).IsTrue();
    }

    [Test]
    public async Task CleanStripsPsqlFraming()
    {
        var psql =
            "                          QUERY PLAN\n" +
            "-----------------------------------------------------------\n" +
            " Seq Scan on t  (cost=0.00..41.88 rows=850 width=4)\n" +
            "   Filter: (id > 3)\n" +
            "(2 rows)";

        var result = ExplainPlanTextParser.Parse(ExplainPlanTextParser.Clean(psql));

        await Assert.That(result.Root.NodeType).IsEqualTo("Seq Scan");
        await Assert.That(result.Root.RelationName).IsEqualTo("t");
        await Assert.That(result.Root.Details.Select(d => d.Key)).Contains("Filter");
    }

    [Test]
    public async Task NeverExecutedBranchParses()
    {
        var text =
            "Nested Loop  (cost=0.00..5.00 rows=1 width=4) (actual time=0.010..0.020 rows=1 loops=1)\n" +
            "  ->  Seq Scan on a  (cost=0.00..1.00 rows=1 width=4) (actual time=0.005..0.006 rows=1 loops=1)\n" +
            "  ->  Index Scan using b_pkey on b  (cost=0.00..1.00 rows=1 width=4) (never executed)";

        var result = ExplainPlanTextParser.Parse(text);

        await Assert.That(result.Root.Children[1].ActualLoops).IsEqualTo(0L);
    }

    [Test]
    public async Task NonPlanTextThrows()
    {
        await Assert.That(() => ExplainService.Import("hello world, this is not a plan"))
            .Throws<FormatException>();
    }
}
