using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// The Core halves of the 2026-09 UI-thread audit (docs/dev/design/ui-thread-audit.md):
/// the pure pieces the views lean on to keep work from growing with the data.
/// </summary>
public class UiThreadAuditTests
{
    private static PlanWarning Warning(string title, PlanWarningSeverity severity = PlanWarningSeverity.Warning, string? kind = null) =>
        new(severity, title, "detail", "Seq Scan", null) { Kind = kind };

    [Test]
    public async Task Condense_keeps_three_of_a_kind_in_plan_order_and_says_how_many_more()
    {
        List<PlanWarning> warnings =
        [
            Warning("Sort spilled to disk"),
            .. Enumerable.Range(0, 1_000).Select(i => Warning($"Row estimate off by {i + 11}×", kind: "Row estimates off")),
            .. Enumerable.Range(0, 5).Select(_ => Warning("Sequential scan discards most rows")),
        ];

        var condensed = PlanAnalyzer.Condense(warnings);

        await Assert.That(condensed.Select(w => w.Title).ToList()).IsEquivalentTo(
        [
            "Sort spilled to disk",
            "Row estimate off by 11×",
            "Row estimate off by 12×",
            "Row estimate off by 13×",
            "Sequential scan discards most rows",
            "Sequential scan discards most rows",
            "Sequential scan discards most rows",
            "997 more: Row estimates off",
            "2 more: Sequential scan discards most rows",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Condense_keeps_severities_apart_and_leaves_a_short_list_alone()
    {
        List<PlanWarning> warnings =
        [
            .. Enumerable.Range(0, 4).Select(_ => Warning("Row estimate off by 20×", kind: "Row estimates off")),
            .. Enumerable.Range(0, 4).Select(_ => Warning("Row estimate off by 500×", PlanWarningSeverity.Critical, "Row estimates off")),
        ];

        var condensed = PlanAnalyzer.Condense(warnings);

        await Assert.That(condensed.Count(w => w.Severity == PlanWarningSeverity.Critical)).IsEqualTo(4);
        await Assert.That(condensed.Count(w => w.Severity == PlanWarningSeverity.Warning)).IsEqualTo(4);
        await Assert.That(condensed.Count(w => w.Title.StartsWith("1 more", StringComparison.Ordinal))).IsEqualTo(2);

        List<PlanWarning> few = [Warning("Sort spilled to disk"), Warning("Hash spilled to disk")];
        await Assert.That(PlanAnalyzer.Condense(few)).IsEquivalentTo(few, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task OneLine_stops_at_its_length_and_marks_the_cut()
    {
        var script = string.Concat(Enumerable.Repeat("INSERT INTO t\n    VALUES (1);\n", 100_000));

        var line = HistoryLabel.OneLine(script, 50);

        await Assert.That(line.Length).IsEqualTo(51);
        await Assert.That(line).IsEqualTo("INSERT INTO t VALUES (1); INSERT INTO t VALUES (1)…");
        await Assert.That(HistoryLabel.OneLine("SELECT 1;", 50)).IsEqualTo("SELECT 1;");
        await Assert.That(HistoryLabel.OneLine(script)).IsEqualTo(HistoryLabel.OneLine(script, int.MaxValue));
    }

    [Test]
    public async Task Tip_cuts_a_long_statement_by_lines_or_characters()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var tip = HistoryLabel.Tip(lines);

        await Assert.That(tip.Split('\n')).Count().IsEqualTo(41);
        await Assert.That(tip).EndsWith("line 40\n…");

        var wide = new string('x', 10_000);
        await Assert.That(HistoryLabel.Tip(wide)).IsEqualTo(new string('x', 4_000) + "\n…");
        await Assert.That(HistoryLabel.Tip("SELECT 1;")).IsEqualTo("SELECT 1;");
    }

    [Test]
    public async Task Trim_drops_the_oldest_unpinned_entries_past_the_cap()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, QueryHistoryStore.MaxEntries + 5)
            .Select(i => new QueryHistoryEntry($"SELECT {i};", now, 1, "1 row", Pinned: i == QueryHistoryStore.MaxEntries + 4))
            .ToList();

        var kept = QueryHistoryStore.Trim(entries);

        await Assert.That(kept.Count).IsEqualTo(QueryHistoryStore.MaxEntries);
        await Assert.That(kept[0].Sql).IsEqualTo("SELECT 0;");
        await Assert.That(kept[^1].Pinned).IsTrue();
        await Assert.That(kept.Any(e => e.Sql == $"SELECT {QueryHistoryStore.MaxEntries};")).IsFalse();
        await Assert.That(QueryHistoryStore.Trim(entries.Take(3))).Count().IsEqualTo(3);
    }

    [Test]
    public async Task A_staged_set_of_every_row_keeps_its_order_through_unstaging()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        for (var id = 0; id < 20_000; id++)
        {
            set.StageDelete([id]);
        }

        set.StageEdit([-1], "status", "shipped");
        for (var id = 0; id < 20_000; id += 2)
        {
            await Assert.That(set.UnstageDelete([id])).IsTrue();
        }

        await Assert.That(set.IsRowDeleted([1])).IsTrue();
        await Assert.That(set.IsRowDeleted([2])).IsFalse();
        await Assert.That(set.IsRowEdited([-1])).IsTrue();
        await Assert.That(set.Count).IsEqualTo(10_001);

        var statements = set.BuildStatements();
        await Assert.That(statements[0].Sql).StartsWith("UPDATE");
        await Assert.That(statements[1].Parameters["pk0"]).IsEqualTo(1);
        await Assert.That(statements[^1].Parameters["pk0"]).IsEqualTo(19_999);
    }
}
