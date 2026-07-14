using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Parser + text-formatter tests over captured `EXPLAIN (FORMAT JSON)` payloads.
/// The PG 18 sample is real output (postgres:18 docker): note the fractional
/// "Actual Rows": 7.00 that used to crash GetInt64()-based parsing.
/// </summary>
public class ExplainParsingTests
{
    private const string Pg18AnalyzeJson = """
        [
          {
            "Plan": {
              "Node Type": "Seq Scan",
              "Parallel Aware": false,
              "Async Capable": false,
              "Relation Name": "t",
              "Alias": "t",
              "Startup Cost": 0.00,
              "Total Cost": 41.88,
              "Plan Rows": 850,
              "Plan Width": 4,
              "Actual Startup Time": 0.009,
              "Actual Total Time": 0.009,
              "Actual Rows": 7.00,
              "Actual Loops": 1,
              "Disabled": false,
              "Filter": "(id > 3)",
              "Rows Removed by Filter": 3
            },
            "Planning Time": 0.181,
            "Triggers": [
            ],
            "Execution Time": 0.021
          }
        ]
        """;

    private const string Pg17AnalyzeJson = """
        [
          {
            "Plan": {
              "Node Type": "Result",
              "Parallel Aware": false,
              "Async Capable": false,
              "Startup Cost": 0.00,
              "Total Cost": 0.01,
              "Plan Rows": 1,
              "Plan Width": 4,
              "Actual Startup Time": 0.001,
              "Actual Total Time": 0.001,
              "Actual Rows": 1,
              "Actual Loops": 1
            },
            "Planning Time": 0.040,
            "Triggers": [
            ],
            "Execution Time": 0.018
          }
        ]
        """;

    private const string JoinPlanJson = """
        [
          {
            "Plan": {
              "Node Type": "Hash Join",
              "Parallel Aware": false,
              "Join Type": "Left",
              "Startup Cost": 1.11,
              "Total Cost": 2.29,
              "Plan Rows": 5,
              "Plan Width": 30,
              "Hash Cond": "(o.customer_id = c.id)",
              "Plans": [
                {
                  "Node Type": "Seq Scan",
                  "Parent Relationship": "Outer",
                  "Parallel Aware": false,
                  "Relation Name": "orders",
                  "Alias": "o",
                  "Startup Cost": 0.00,
                  "Total Cost": 1.05,
                  "Plan Rows": 5,
                  "Plan Width": 12
                },
                {
                  "Node Type": "Hash",
                  "Parent Relationship": "Inner",
                  "Parallel Aware": false,
                  "Startup Cost": 1.05,
                  "Total Cost": 1.05,
                  "Plan Rows": 5,
                  "Plan Width": 22,
                  "Plans": [
                    {
                      "Node Type": "Index Scan",
                      "Parent Relationship": "Outer",
                      "Parallel Aware": false,
                      "Scan Direction": "Forward",
                      "Index Name": "customers_pkey",
                      "Relation Name": "customers",
                      "Alias": "c",
                      "Startup Cost": 0.00,
                      "Total Cost": 1.05,
                      "Plan Rows": 5,
                      "Plan Width": 22
                    }
                  ]
                }
              ]
            },
            "Planning Time": 0.120
          }
        ]
        """;

    [Test]
    public async Task Pg18FractionalActualRowsParse()
    {
        var result = ExplainService.Parse(Pg18AnalyzeJson);

        await Assert.That(result.Root.ActualRows).IsEqualTo(7.0);
        await Assert.That(result.Root.PlanRows).IsEqualTo(850L);
        await Assert.That(result.ExecutionTimeMs).IsEqualTo(0.021);
    }

    [Test]
    public async Task Pg17IntegerActualRowsStillParse()
    {
        var result = ExplainService.Parse(Pg17AnalyzeJson);

        await Assert.That(result.Root.ActualRows).IsEqualTo(1.0);
        await Assert.That(result.Root.ActualLoops).IsEqualTo(1L);
    }

    [Test]
    public async Task DetailPropertiesAreKeptStructuralOnesAreNot()
    {
        var root = ExplainService.Parse(Pg18AnalyzeJson).Root;
        var keys = root.Details.Select(d => d.Key).ToList();

        await Assert.That(keys).Contains("Filter");
        await Assert.That(keys).Contains("Rows Removed by Filter");
        // Structural / noise keys must not leak into the detail lines.
        await Assert.That(keys).DoesNotContain("Node Type");
        await Assert.That(keys).DoesNotContain("Parallel Aware");
        await Assert.That(keys).DoesNotContain("Disabled");
    }

    [Test]
    public async Task TextFormatMatchesPostgresLayout()
    {
        var text = ExplainTextFormatter.Format(ExplainService.Parse(Pg18AnalyzeJson));

        var expected =
            "Seq Scan on t  (cost=0.00..41.88 rows=850 width=4) (actual time=0.009..0.009 rows=7 loops=1)\n" +
            "  Filter: (id > 3)\n" +
            "  Rows Removed by Filter: 3\n" +
            "Planning Time: 0.181 ms\n" +
            "Execution Time: 0.021 ms";
        await Assert.That(text.ReplaceLineEndings("\n")).IsEqualTo(expected);
    }

    [Test]
    public async Task TextFormatIndentsChildrenWithArrows()
    {
        var text = ExplainTextFormatter.Format(ExplainService.Parse(JoinPlanJson));

        var expected =
            "Hash Left Join  (cost=1.11..2.29 rows=5 width=30)\n" +
            "  Hash Cond: (o.customer_id = c.id)\n" +
            "  ->  Seq Scan on orders o  (cost=0.00..1.05 rows=5 width=12)\n" +
            "  ->  Hash  (cost=1.05..1.05 rows=5 width=22)\n" +
            "        ->  Index Scan using customers_pkey on customers c  (cost=0.00..1.05 rows=5 width=22)\n" +
            "Planning Time: 0.120 ms";
        await Assert.That(text.ReplaceLineEndings("\n")).IsEqualTo(expected);
    }

    [Test]
    public async Task FractionalActualRowsKeepTheirDecimals()
    {
        var json = Pg18AnalyzeJson.Replace("\"Actual Rows\": 7.00", "\"Actual Rows\": 7.50");
        var text = ExplainTextFormatter.Format(ExplainService.Parse(json));

        await Assert.That(text).Contains("rows=7.5 loops=1");
    }
}
