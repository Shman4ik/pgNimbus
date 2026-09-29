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
    public async Task A_plan_deeper_than_json_documents_default_depth_still_parses()
    {
        // Each plan level is an object and a "Plans" array, so JsonDocument's
        // default depth of 64 stopped a plan at about 31 levels: a join of that
        // many tables. 40 levels reads now, in JSON and in text alike.
        const int levels = 40;
        var node = """{"Node Type": "Result", "Startup Cost": 0, "Total Cost": 1, "Plan Rows": 1, "Plan Width": 4}""";
        for (var i = 0; i < levels - 1; i++)
        {
            node = $$"""{"Node Type": "Nested Loop", "Startup Cost": 0, "Total Cost": 1, "Plan Rows": 1, "Plan Width": 4, "Plans": [{{node}}]}""";
        }

        var json = ExplainService.Parse($$"""[{"Plan": {{node}}}]""");
        var text = ExplainPlanTextParser.Parse(ExplainTextFormatter.Format(json));

        await Assert.That(Depth(json.Root)).IsEqualTo(levels);
        await Assert.That(Depth(text.Root)).IsEqualTo(levels);

        static int Depth(ExplainNode n) => 1 + (n.Children.Count == 0 ? 0 : n.Children.Max(Depth));
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
    public async Task BufferedAnalyzePlanParsesAndKeepsTrailerOffTheNodes()
    {
        // Exactly what `EXPLAIN (ANALYZE, BUFFERS)` prints — the shape a hand-written
        // EXPLAIN in the editor produces, now parsed back into the plan views. The
        // trailing "Planning:" section's buffers describe the statement, not the last
        // node parsed, so they must not land in that node's details (they feed the
        // buffers heat metric and the analyzer).
        var text =
            "Sort  (cost=27820.64..28320.64 rows=200000 width=4) (actual time=77.770..92.000 rows=200000.00 loops=1)\n" +
            "  Sort Key: g DESC\n" +
            "  Sort Method: external merge  Disk: 2400kB\n" +
            "  Buffers: shared hit=3, temp read=1236 written=1308\n" +
            "  ->  Function Scan on generate_series g  (cost=0.00..2000.00 rows=200000 width=4) (actual time=20.000..40.000 rows=200000.00 loops=1)\n" +
            "        Buffers: temp read=342 written=342\n" +
            "Planning:\n" +
            "  Buffers: shared hit=9\n" +
            "Planning Time: 0.079 ms\n" +
            "Execution Time: 102.734 ms";

        var result = ExplainPlanTextParser.Parse(ExplainPlanTextParser.Clean(text));

        await Assert.That(result.Root.NodeType).IsEqualTo("Sort");
        await Assert.That(result.ExecutionTimeMs).IsEqualTo(102.734);
        await Assert.That(result.Root.Details.Select(d => d.Key)).Contains("Sort Method");

        // The function scan keeps its own Buffers line and gains no second one from
        // the Planning trailer.
        var scan = result.Root.Children[0];
        await Assert.That(scan.NodeType).IsEqualTo("Function Scan");
        await Assert.That(scan.Details.Count(d => d.Key == "Buffers")).IsEqualTo(1);
        await Assert.That(scan.Details.First(d => d.Key == "Buffers").Value).IsEqualTo("temp read=342 written=342");

        // …and the spill in that sort is exactly what the analyzer should flag.
        await Assert.That(PlanAnalyzer.Analyze(result).Any(w => w.Title == "Sort spilled to disk")).IsTrue();
    }

    [Test]
    public async Task NonPlanTextThrows()
    {
        await Assert.That(() => ExplainService.Import("hello world, this is not a plan"))
            .Throws<FormatException>();
    }

    // --- Security audit 2026-09, finding 16: every parse failure is a FormatException ---

    [Test]
    public async Task CostsOffJsonParsesWithZeroFigures()
    {
        // EXPLAIN (FORMAT JSON, COSTS OFF) carries no Startup Cost / Total Cost /
        // Plan Rows / Plan Width at all; GetProperty on them was a KeyNotFoundException
        // that reached the crash window.
        const string costsOff = """
            [
              {
                "Plan": {
                  "Node Type": "Hash Join",
                  "Join Type": "Inner",
                  "Plans": [
                    { "Node Type": "Seq Scan", "Relation Name": "t", "Alias": "t" },
                    { "Node Type": "Hash", "Plans": [ { "Node Type": "Seq Scan", "Relation Name": "u", "Alias": "u" } ] }
                  ]
                }
              }
            ]
            """;

        var imported = ExplainService.Import(costsOff);

        await Assert.That(imported.Result.Root.NodeType).IsEqualTo("Hash Join");
        await Assert.That(imported.Result.Root.TotalCost).IsEqualTo(0);
        await Assert.That(imported.Result.Root.PlanRows).IsEqualTo(0);
        await Assert.That(imported.Result.Root.Children.Count).IsEqualTo(2);
        await Assert.That(imported.Result.Root.Children[1].Children[0].RelationName).IsEqualTo("u");
        // The text view and the analyzer take the figure-less tree in their stride.
        await Assert.That(imported.DisplayText).StartsWith("Hash Join");
        _ = PlanAnalyzer.Analyze(imported.Result);
    }

    [Test]
    [Arguments("""[{"Plan": 5}]""")]
    [Arguments("""[{"Plan": {"Startup Cost": 1}}]""")]
    [Arguments("""[{"Plan": {"Node Type": 7}}]""")]
    [Arguments("""[{"Plan": {"Node Type": "Result", "Plans": 3}}]""")]
    [Arguments("""{"Plan": []}""")]
    [Arguments("""[5]""")]
    [Arguments("""[]""")]
    [Arguments("""[{"Plan": {"Node Type": "Result"}""")]
    [Arguments("Seq Scan on t  (cost=0.00..1.00 rows=99999999999999999999 width=4)\n  ->  Bogus line with no cost")]
    [Arguments("  ->  Seq Scan on t  (cost=0.00..1.00 rows=1 width=4)\n->  Seq Scan on t  (cost=0.00..1.00 rows=1 width=4)")]
    public async Task WrongShapedInputIsAFormatException(string raw)
    {
        // [{"Plan": 5}] was an InvalidOperationException, a node without "Node Type" a
        // KeyNotFoundException, rows= past 9.2e18 an OverflowException — each escaped
        // the import dialog's catch (FormatException only) and shut the app down.
        await Assert.That(() => ExplainService.Import(raw)).Throws<FormatException>();
    }

    [Test]
    public async Task RowCountsPastALongSaturateInsteadOfOverflowing()
    {
        var json = ExplainService.Parse("""{ "Node Type": "Result", "Plan Rows": 1e30, "Plan Width": 1e12, "Actual Loops": -1e30, "Actual Rows": "many" }""");
        await Assert.That(json.Root.PlanRows).IsEqualTo(long.MaxValue);
        await Assert.That(json.Root.PlanWidth).IsEqualTo(int.MaxValue);
        await Assert.That(json.Root.ActualLoops).IsEqualTo(long.MinValue);
        // A number written as a string is not a number.
        await Assert.That(json.Root.ActualRows).IsNull();

        var text = ExplainService.Import("Seq Scan on t  (cost=0.00..1.00 rows=99999999999999999999 width=4)");
        await Assert.That(text.Result.Root.PlanRows).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task JsonNestedPastTheReadersDepthIsAFormatException()
    {
        // Parse reads JSON to ExplainService.MaxJsonDepth (256, about 127 plan nodes
        // deep); past it JsonDocument throws a JsonException, which used to escape
        // Parse (only Import translated it). 130 levels is 260 JSON levels.
        var deep = string.Concat(Enumerable.Repeat("""{"Node Type": "Result", "Plans": [""", 130)) + "{\"Node Type\": \"Result\"}" + string.Concat(Enumerable.Repeat("]}", 130));

        await Assert.That(() => ExplainService.Parse(deep)).Throws<FormatException>();
        await Assert.That(() => ExplainService.Import(deep)).Throws<FormatException>();
    }
}
