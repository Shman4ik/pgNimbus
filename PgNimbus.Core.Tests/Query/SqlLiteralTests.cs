using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

public class SqlLiteralTests
{
    [Test]
    public async Task NullRendersAsKeyword()
    {
        await Assert.That(SqlLiteral.Format(null)).IsEqualTo("NULL");
    }

    [Test]
    public async Task StringsQuoteAndDoubleEmbeddedQuotes()
    {
        await Assert.That(SqlLiteral.Format("O'Brien")).IsEqualTo("'O''Brien'");
    }

    [Test]
    public async Task BooleansRenderBare()
    {
        await Assert.That(SqlLiteral.Format(true)).IsEqualTo("true");
        await Assert.That(SqlLiteral.Format(false)).IsEqualTo("false");
    }

    [Test]
    public async Task NumbersUseInvariantCulture()
    {
        await Assert.That(SqlLiteral.Format(42)).IsEqualTo("42");
        await Assert.That(SqlLiteral.Format(12.5m)).IsEqualTo("12.5");
        await Assert.That(SqlLiteral.Format(0.25)).IsEqualTo("0.25");
    }

    [Test]
    public async Task DatesAndTimesRenderIsoQuoted()
    {
        await Assert.That(SqlLiteral.Format(new DateOnly(2026, 7, 14))).IsEqualTo("'2026-07-14'");
        await Assert.That(SqlLiteral.Format(new DateTime(2026, 7, 14, 8, 30, 0))).IsEqualTo("'2026-07-14 08:30:00'");
        await Assert.That(SqlLiteral.Format(new TimeOnly(8, 30, 15))).IsEqualTo("'08:30:15'");
    }

    [Test]
    public async Task OtherTypesFallBackToQuotedText()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");

        await Assert.That(SqlLiteral.Format(guid)).IsEqualTo("'11111111-2222-3333-4444-555555555555'");
    }
}
