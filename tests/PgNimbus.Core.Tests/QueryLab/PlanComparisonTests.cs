using System.Text.RegularExpressions;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;
using static PgNimbus.Core.Tests.QueryLab.Plans;

namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>
/// The summary of two plans and its notes (<see cref="PlanComparison"/>). The issue's
/// constraints are tested as such: cost is never time, a figure that wasn't measured is
/// never a zero, and nothing calls one run a win.
/// </summary>
public class PlanComparisonTests
{
    private static PlanComparison Real(string before, string after) =>
        PlanComparison.Compare(
            new PlanSide(ExplainService.Import(before).Result, "17.11", "127.0.0.1/postgres", "SELECT …"),
            new PlanSide(ExplainService.Import(after).Result, "17.11", "127.0.0.1/postgres", "SELECT …"));

    private static PlanMetricRow Row(PlanComparison comparison, string label) =>
        comparison.Metrics.Single(r => r.Label == label);

    private static IEnumerable<string> Titles(PlanComparison comparison) => comparison.Notes.Select(n => n.Title);

    [Test]
    public async Task The_real_index_change_compares_figure_by_figure()
    {
        var comparison = Real(RealPlans.BeforeJson, RealPlans.AfterJson);

        var time = Row(comparison, "Execution time");
        await Assert.That(time.Before).IsEqualTo(11.406);
        await Assert.That(time.After).IsEqualTo(0.365);
        await Assert.That(time.ChangeText).IsEqualTo("−11 ms (−96.8%)");
        await Assert.That(time.Note).IsEqualTo("one run each");
        await Assert.That(Row(comparison, "Shared buffers hit").BeforeText).IsEqualTo("2,948");
        await Assert.That(Row(comparison, "Rows returned").ChangeText).IsEqualTo("no change");
        await Assert.That(Row(comparison, "Estimated cost (planner units)").Unit).IsEqualTo(PlanMetricUnit.PlannerUnits);
        await Assert.That(Titles(comparison)).Contains("One run each");
        await Assert.That(Titles(comparison)).Contains("The plan's shape changed");
        await Assert.That(Titles(comparison)).DoesNotContain("The cache may explain part of the difference");
        await Assert.That(comparison.Settings).IsEmpty();
        // The before plan's sequential scan read 400,000 rows to keep 40.
        await Assert.That(comparison.WarningsResolved.Select(w => w.Title)).Contains("Sequential scan discards most rows");
        await Assert.That(comparison.WarningsIntroduced).IsEmpty();
    }

    [Test]
    public async Task A_cold_before_run_against_a_warm_after_run_warns_about_the_cache()
    {
        var comparison = Real(RealPlans.BeforeColdJson, RealPlans.AfterJson);
        var cache = comparison.Notes.Single(n => n.Title == "The cache may explain part of the difference");

        await Assert.That(cache.Severity).IsEqualTo(PlanWarningSeverity.Warning);
        await Assert.That(cache.Detail).Contains("The before run read 2,945 blocks");
        await Assert.That(Row(comparison, "Shared buffers read").Before).IsEqualTo(2945);
    }

    [Test]
    public async Task A_plain_explain_side_says_why_its_figures_are_missing()
    {
        var plain = Plan(Node("Seq Scan", relation: "t", alias: "t", cost: 35.5, rows: 2550), capture: Measured(buffers: false));
        var analyzed = Plan(Node("Seq Scan", relation: "t", alias: "t", cost: 35.5, rows: 2550).Ran(0.4, 2550), executionMs: 0.5, capture: Measured());

        var comparison = PlanComparison.Compare(new PlanSide(plain), new PlanSide(analyzed));

        await Assert.That(Row(comparison, "Execution time").BeforeMissing).IsEqualTo("not analyzed");
        await Assert.That(Row(comparison, "Execution time").BeforeText).IsEqualTo("—");
        await Assert.That(Row(comparison, "Execution time").ChangeText).IsEmpty();
        await Assert.That(Row(comparison, "Rows returned").BeforeMissing).IsEqualTo("not analyzed");
        await Assert.That(Row(comparison, "Shared buffers hit").BeforeMissing).IsEqualTo("not analyzed");
        await Assert.That(Row(comparison, "Estimated cost (planner units)").ChangeText).IsEqualTo("no change");
        var note = comparison.Notes.Single(n => n.Title == "Estimates only");
        await Assert.That(note.Detail).Contains("not time");
        await Assert.That(Titles(comparison)).DoesNotContain("One run each");
    }

    [Test]
    public async Task Missing_buffers_are_reported_as_missing_not_as_zero()
    {
        var withBuffers = Plan(Node("Seq Scan", relation: "t", alias: "t").Ran(1, 10), executionMs: 1, capture: Measured());
        var without = Plan(Node("Seq Scan", relation: "t", alias: "t").Ran(1, 10), executionMs: 1, capture: Measured(buffers: false));
        var unknown = Plan(Node("Seq Scan", relation: "t", alias: "t").Ran(1, 10), executionMs: 1,
            capture: new ExplainCapture { Format = PlanFormat.Text });

        var off = PlanComparison.Compare(new PlanSide(withBuffers), new PlanSide(without));
        var cantTell = PlanComparison.Compare(new PlanSide(unknown), new PlanSide(withBuffers));

        await Assert.That(Row(off, "Shared buffers read").After).IsNull();
        await Assert.That(Row(off, "Shared buffers read").AfterMissing).IsEqualTo("no BUFFERS");
        await Assert.That(Row(cantTell, "Shared buffers read").BeforeMissing).IsEqualTo("BUFFERS unknown");
        await Assert.That(off.Notes.Single(n => n.Title == "Cache use unknown").Detail).StartsWith("The after plan has no buffer counts");
    }

    [Test]
    public async Task Timing_off_compares_only_the_totals()
    {
        var timed = Plan(Node("Seq Scan", relation: "t", alias: "t").Ran(1, 10), executionMs: 1, capture: Measured());
        var untimed = Plan(Node("Seq Scan", relation: "t", alias: "t") with { ActualRows = 10, ActualLoops = 1 }, executionMs: 1.2, capture: Measured());

        var comparison = PlanComparison.Compare(new PlanSide(timed), new PlanSide(untimed));

        await Assert.That(Titles(comparison)).Contains("Timing was off");
        await Assert.That(Row(comparison, "Execution time").After).IsEqualTo(1.2);
    }

    [Test]
    public async Task Context_that_differs_is_named()
    {
        var a = Plan(Node("Seq Scan", relation: "t", alias: "t").Ran(1, 10), executionMs: 1,
            capture: Measured(true, ("random_page_cost", "4"), ("work_mem", "4MB")));
        var b = Plan(Node("Seq Scan", relation: "t", alias: "t").Ran(1, 12), executionMs: 1,
            capture: Measured(true, ("random_page_cost", "1.1"), ("enable_seqscan", "off")) with { JitMs = 5.4 });

        var comparison = PlanComparison.Compare(
            new PlanSide(a, "16.4", "db1/app", "SELECT * FROM t"),
            new PlanSide(b, "17.2", "db2/app", "SELECT * FROM t WHERE true"));

        await Assert.That(Titles(comparison)).Contains("Different PostgreSQL versions");
        await Assert.That(Titles(comparison)).Contains("Costs are on different scales");
        await Assert.That(Titles(comparison)).Contains("JIT compiled one side only");
        await Assert.That(Titles(comparison)).Contains("Different connections");
        await Assert.That(Titles(comparison)).Contains("Different statements");
        await Assert.That(Titles(comparison)).Contains("Different rows returned");
        await Assert.That(comparison.Settings.Select(s => $"{s.Name}:{s.Before ?? "default"}→{s.After ?? "default"}")).IsEquivalentTo(
            ["enable_seqscan:default→off", "random_page_cost:4→1.1", "work_mem:4MB→default"], CollectionOrdering.Matching);
        await Assert.That(Row(comparison, "JIT time").Before).IsEqualTo(0);
        await Assert.That(Row(comparison, "JIT time").After).IsEqualTo(5.4);
    }

    [Test]
    public async Task The_same_statement_spaced_differently_is_the_same_statement()
    {
        var plan = Plan(Node("Seq Scan", relation: "t", alias: "t").Ran(1, 10), executionMs: 1, capture: Measured());

        var comparison = PlanComparison.Compare(
            new PlanSide(plan, Statement: "SELECT *\n  FROM t;"),
            new PlanSide(plan, Statement: "SELECT * FROM t"));

        await Assert.That(Titles(comparison)).DoesNotContain("Different statements");
    }

    [Test]
    public async Task Settings_unknown_on_one_side_are_not_listed_as_changes()
    {
        var known = Plan(Node("Result"), capture: Measured(true, ("work_mem", "64MB")));
        var unknown = Plan(Node("Result"), capture: new ExplainCapture { Format = PlanFormat.Text });

        var comparison = PlanComparison.Compare(new PlanSide(known), new PlanSide(unknown));

        await Assert.That(comparison.Settings).IsEmpty();
        await Assert.That(Titles(comparison)).Contains("Settings unknown on one side");
    }

    [Test]
    public async Task Fewer_workers_than_planned_is_a_warning()
    {
        ExplainNode Gather(string launched) =>
            Node("Gather", details: [("Workers Planned", "4"), ("Workers Launched", launched)],
                children: [Node("Seq Scan", relation: "t", alias: "t", parallel: true).Ran(5, 10, loops: 2)]).Ran(6, 20);

        var comparison = PlanComparison.Compare(
            new PlanSide(Plan(Gather("4"), executionMs: 6, capture: Measured())),
            new PlanSide(Plan(Gather("1"), executionMs: 6, capture: Measured())));

        await Assert.That(comparison.Notes.Single(n => n.Title == "Fewer parallel workers than planned").Detail)
            .Contains("in the after run");
    }

    [Test]
    public async Task A_row_estimate_warning_with_another_factor_is_still_the_same_warning()
    {
        // "Row estimate off by 120×" before and "…off by 300×" after, on the same scan:
        // neither resolved nor introduced.
        ExplainResult Misestimated(double actual) =>
            Plan(Node("Seq Scan", relation: "t", alias: "t", rows: 1000).Ran(1, actual), executionMs: 1, capture: Measured());

        var comparison = PlanComparison.Compare(new PlanSide(Misestimated(120_000)), new PlanSide(Misestimated(300_000)));

        await Assert.That(comparison.WarningsResolved).IsEmpty();
        await Assert.That(comparison.WarningsIntroduced).IsEmpty();
    }

    [Test]
    [Arguments(811.2, 0.4, PlanMetricUnit.Milliseconds, "−810.8 ms (−100%)")]
    [Arguments(10, 12.5, PlanMetricUnit.Milliseconds, "+2.5 ms (+25%)")]
    [Arguments(0, 5, PlanMetricUnit.Blocks, "+5")]
    [Arguments(12004, 3, PlanMetricUnit.Blocks, "−12,001 (−100%)")]
    [Arguments(24512.4, 8.6, PlanMetricUnit.PlannerUnits, "−24,504 (−100%)")]
    [Arguments(42000, 41000, PlanMetricUnit.Milliseconds, "−1,000 ms (−2.4%)")]
    public async Task Changes_are_signed_and_say_nothing_else(double before, double after, PlanMetricUnit unit, string expected)
    {
        await Assert.That(new PlanMetricRow("x", before, after, unit).ChangeText).IsEqualTo(expected);
    }

    [Test]
    public async Task Nothing_the_comparison_writes_is_a_verdict()
    {
        // The issue's constraint, held over every string every comparison here produces.
        var verdict = new Regex(@"\b(faster|slower|improv\w*|better|worse|regress\w*|win|wins|winner)\b", RegexOptions.IgnoreCase);
        var comparisons = new[]
        {
            Real(RealPlans.BeforeJson, RealPlans.AfterJson),
            Real(RealPlans.BeforeColdJson, RealPlans.AfterJson),
            Real(RealPlans.AfterJson, RealPlans.BeforeJson),
            Real(RealPlans.BeforeText, RealPlans.AfterText),
            PlanComparison.Compare(
                new PlanSide(Plan(Node("Seq Scan", relation: "t", alias: "t"), capture: Measured(false, ("work_mem", "1MB"))), "16.1", "a/b", "x"),
                new PlanSide(Plan(Node("Index Scan", relation: "t", alias: "t", index: "i").Ran(1, 1), 1, Measured(true, ("seq_page_cost", "2")) with { JitMs = 1, TriggerMs = 2 }), "17.1", "c/d", "y")),
        };

        var strings = comparisons.SelectMany(c =>
            c.Metrics.SelectMany(r => new[] { r.Label, r.BeforeText, r.AfterText, r.ChangeText, r.Note, r.BeforeMissing, r.AfterMissing })
                .Concat(c.Notes.SelectMany(n => new[] { n.Title, n.Detail }))
                .Concat(All(c.Diff.Roots).SelectMany(n => n.Differences.Append(n.Header))))
            .OfType<string>()
            .ToList();

        await Assert.That(strings.Count).IsGreaterThan(50);
        await Assert.That(strings.Where(s => verdict.IsMatch(s))).IsEmpty();
    }
}
