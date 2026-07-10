using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class AutoClosePairsTests
{
    [Test]
    [Arguments("SELECT coalesce", 15, '(')]
    [Arguments("SELECT ( FROM t", 7, '(')] // before whitespace
    [Arguments("SELECT f())", 9, '(')] // before an existing closer
    [Arguments("WHERE name = ", 13, '\'')]
    [Arguments("SELECT ", 7, '"')]
    public async Task Opener_BeforeBoundary_InsertsPair(string text, int caret, char typed)
    {
        await Assert.That(AutoClosePairs.Decide(text, caret, typed, inStringOrComment: false))
            .IsEqualTo(AutoClosePairs.Verdict.InsertPair);
    }

    [Test]
    [Arguments("SELECT name FROM t", 7, '(')] // directly before a word
    [Arguments("WHERE x = 'abc'", 10, '\'')] // directly before another quote
    [Arguments("SELECT \"col\"", 7, '"')]
    public async Task Opener_BeforeWordOrQuote_DoesNothing(string text, int caret, char typed)
    {
        await Assert.That(AutoClosePairs.Decide(text, caret, typed, inStringOrComment: false))
            .IsEqualTo(AutoClosePairs.Verdict.None);
    }

    [Test]
    public async Task Opener_InsideStringOrComment_DoesNothing()
    {
        // The apostrophe in a comment, the paren in a string: plain characters.
        await Assert.That(AutoClosePairs.Decide("-- don", 6, '\'', inStringOrComment: true))
            .IsEqualTo(AutoClosePairs.Verdict.None);
        await Assert.That(AutoClosePairs.Decide("'a ", 3, '(', inStringOrComment: true))
            .IsEqualTo(AutoClosePairs.Verdict.None);
    }

    [Test]
    public async Task ClosingParen_BeforeClosingParen_TypesOver()
    {
        // The caret sits between the parens "coalesce()" completion left behind.
        await Assert.That(AutoClosePairs.Decide("coalesce()", 9, ')', inStringOrComment: false))
            .IsEqualTo(AutoClosePairs.Verdict.TypeOver);
    }

    [Test]
    public async Task ClosingParen_ElsewhereOrInString_Inserts()
    {
        await Assert.That(AutoClosePairs.Decide("f(x", 3, ')', inStringOrComment: false))
            .IsEqualTo(AutoClosePairs.Verdict.None);
        await Assert.That(AutoClosePairs.Decide("'())'", 3, ')', inStringOrComment: true))
            .IsEqualTo(AutoClosePairs.Verdict.None);
    }

    [Test]
    public async Task Quote_BeforeSameQuote_TypesOver_EvenInsideTheString()
    {
        // 'abc|' — typing ' closes the literal by stepping over its closer.
        await Assert.That(AutoClosePairs.Decide("'abc'", 4, '\'', inStringOrComment: true))
            .IsEqualTo(AutoClosePairs.Verdict.TypeOver);
        await Assert.That(AutoClosePairs.Decide("\"col\"", 4, '"', inStringOrComment: true))
            .IsEqualTo(AutoClosePairs.Verdict.TypeOver);
    }

    [Test]
    public async Task Quote_InsideStringNotAtCloser_DoesNothing()
    {
        // 'it|s ' — an interior apostrophe must not auto-pair.
        await Assert.That(AutoClosePairs.Decide("'its '", 3, '\'', inStringOrComment: true))
            .IsEqualTo(AutoClosePairs.Verdict.None);
    }

    [Test]
    public async Task AtEndOfText_OpenersPair_ClosersInsert()
    {
        await Assert.That(AutoClosePairs.Decide("", 0, '(', inStringOrComment: false))
            .IsEqualTo(AutoClosePairs.Verdict.InsertPair);
        await Assert.That(AutoClosePairs.Decide("f(x", 3, ')', inStringOrComment: false))
            .IsEqualTo(AutoClosePairs.Verdict.None);
    }

    [Test]
    public async Task CloserFor_MapsOpeners()
    {
        await Assert.That(AutoClosePairs.CloserFor('(')).IsEqualTo(')');
        await Assert.That(AutoClosePairs.CloserFor('\'')).IsEqualTo('\'');
        await Assert.That(AutoClosePairs.CloserFor('"')).IsEqualTo('"');
    }
}
