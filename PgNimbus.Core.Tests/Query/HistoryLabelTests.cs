using PgNimbus.Core.Query;
using TUnit.Core.Executors;

namespace PgNimbus.Core.Tests.Query;

public class HistoryLabelTests
{
    // A Thursday, in a zone east of UTC so day boundaries are exercised in the
    // offset of "now", not in UTC.
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 9, 41, 0, TimeSpan.FromHours(2));

    [Test]
    public async Task OneLineFoldsLineBreaksAndIndentation()
    {
        const string sql = "SELECT o.id,\n       c.full_name\n  FROM orders AS o\r\n\t WHERE o.paid;\n";
        await Assert.That(HistoryLabel.OneLine(sql)).IsEqualTo("SELECT o.id, c.full_name FROM orders AS o WHERE o.paid;");
    }

    [Test]
    public async Task OneLineTrimsBothEnds()
    {
        await Assert.That(HistoryLabel.OneLine("  \n SELECT 1; \n ")).IsEqualTo("SELECT 1;");
        await Assert.That(HistoryLabel.OneLine("   ")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TodayIsJustTheTime()
    {
        await Assert.That(HistoryLabel.When(Now.AddMinutes(-22), Now)).IsEqualTo("09:19");
    }

    [Test]
    public async Task DaysAreCountedInTheOffsetOfNow()
    {
        // 23:30 UTC on the 29th is 01:30 on the 30th two hours east: today.
        var lateUtc = new DateTimeOffset(2026, 7, 29, 23, 30, 0, TimeSpan.Zero);
        await Assert.That(HistoryLabel.When(lateUtc, Now)).IsEqualTo("01:30");
    }

    [Test]
    public async Task YesterdayAndTheWeekNameTheDay()
    {
        await Assert.That(HistoryLabel.When(Now.AddDays(-1).AddHours(5), Now)).IsEqualTo("Yesterday 14:41");
        await Assert.That(HistoryLabel.When(Now.AddDays(-3), Now)).IsEqualTo("Mon 09:41");
    }

    [Test]
    // A culture with its own month and day names: the label must stay English.
    [Culture("cs-CZ")]
    public async Task OlderDatesAreInvariant()
    {
        await Assert.That(HistoryLabel.When(Now.AddDays(-3), Now)).IsEqualTo("Mon 09:41");
        await Assert.That(HistoryLabel.When(Now.AddDays(-12), Now)).IsEqualTo("Jul 18");
        await Assert.That(HistoryLabel.When(Now.AddYears(-1), Now)).IsEqualTo("2025-07-30");
    }

    [Test]
    [Culture("fr-FR")]
    public async Task ElapsedPicksItsUnit()
    {
        await Assert.That(HistoryLabel.Elapsed(0.4)).IsEqualTo("<1 ms");
        await Assert.That(HistoryLabel.Elapsed(18.4)).IsEqualTo("18 ms");
        await Assert.That(HistoryLabel.Elapsed(942)).IsEqualTo("942 ms");
        await Assert.That(HistoryLabel.Elapsed(1234)).IsEqualTo("1.2 s");
        await Assert.That(HistoryLabel.Elapsed(125_000)).IsEqualTo("2m 05s");
    }

    [Test]
    public async Task MetaNamesTheConnectionOnlyWhenItIsAnotherOne()
    {
        var entry = new QueryHistoryEntry("SELECT 1;", Now.AddMinutes(-6), 4.1, "1 row", Connection: "localhost/shop");
        await Assert.That(HistoryLabel.Meta(entry, "localhost/shop", Now)).IsEqualTo("09:35 · 4 ms · 1 row");
        await Assert.That(HistoryLabel.Meta(entry, "prod/shop", Now)).IsEqualTo("09:35 · 4 ms · 1 row · localhost/shop");
    }

    [Test]
    public async Task MetaSkipsAnEmptySummaryAndAMissingConnection()
    {
        var entry = new QueryHistoryEntry("SET x = 1;", Now, 0.2, "");
        await Assert.That(HistoryLabel.Meta(entry, "localhost/shop", Now)).IsEqualTo("09:41 · <1 ms");
    }

    [Test]
    public async Task DetailHasTheFullTimestampAndTheConnection()
    {
        var entry = new QueryHistoryEntry("SELECT 1;", Now.AddSeconds(-5), 4.1, "1 row", Connection: "localhost/shop");
        await Assert.That(HistoryLabel.Detail(entry, Now)).IsEqualTo("Ran 2026-07-30 09:40:55 on localhost/shop");
        await Assert.That(HistoryLabel.Detail(entry with { Connection = null }, Now)).IsEqualTo("Ran 2026-07-30 09:40:55");
    }
}
