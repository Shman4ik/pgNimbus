using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class FuzzyMatcherTests
{
    [Test]
    public async Task EmptyQuery_MatchesEverything_WithZeroScore()
    {
        await Assert.That(FuzzyMatcher.Score("orders", "")).IsEqualTo(0);
    }

    [Test]
    public async Task NonSubsequence_DoesNotMatch()
    {
        await Assert.That(FuzzyMatcher.Score("orders", "dro")).IsNull();
    }

    [Test]
    public async Task QueryLongerThanTarget_DoesNotMatch()
    {
        await Assert.That(FuzzyMatcher.Score("id", "identifier")).IsNull();
    }

    [Test]
    public async Task Matching_IsCaseInsensitive()
    {
        await Assert.That(FuzzyMatcher.Score("SELECT", "sel")).IsNotNull();
        await Assert.That(FuzzyMatcher.Score("daily_revenue", "DR")).IsNotNull();
    }

    [Test]
    public async Task WordBoundarySubsequence_Matches()
    {
        // The completion popup relies on this: "dr" must find daily_revenue.
        await Assert.That(FuzzyMatcher.Score("daily_revenue", "dr")).IsNotNull();
    }

    [Test]
    public async Task PrefixRun_OutscoresScatteredMatch()
    {
        var prefix = FuzzyMatcher.Score("customers", "cust");
        var scattered = FuzzyMatcher.Score("created_at_customs", "cust");

        await Assert.That(prefix!.Value).IsGreaterThan(scattered!.Value);
    }
}
