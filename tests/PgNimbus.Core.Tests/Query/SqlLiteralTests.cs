using PgNimbus.Core.Query;
using TUnit.Core.Executors;

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
    public async Task A_backslash_makes_an_escape_string_that_reads_the_same_under_either_setting()
    {
        // Plain text keeps the plain form.
        await Assert.That(SqlLiteral.Quote("plain")).IsEqualTo("'plain'");

        // With standard_conforming_strings off (a pooler that dropped the
        // startup option), '\'' in a plain literal would end it early; an
        // E-string with the backslash doubled means the same text either way.
        await Assert.That(SqlLiteral.Quote(@"a\b")).IsEqualTo(@"E'a\\b'");
        await Assert.That(SqlLiteral.Quote(@"x\'' OR 1=1 --")).IsEqualTo(@"E'x\\'''' OR 1=1 --'");
    }

    [Test]
    public async Task BooleansRenderBare()
    {
        await Assert.That(SqlLiteral.Format(true)).IsEqualTo("true");
        await Assert.That(SqlLiteral.Format(false)).IsEqualTo("false");
    }

    [Test]
    // A decimal-comma culture: the output must still use a dot.
    [Culture("fr-FR")]
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
