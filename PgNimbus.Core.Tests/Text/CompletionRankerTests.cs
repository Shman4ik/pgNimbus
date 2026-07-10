using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class CompletionRankerTests
{
    private sealed record Candidate(string Text, double Priority = 0);

    private static readonly CompletionRecency NoRecency = new();

    private static CompletionRanker.Ranked<Candidate> Rank(
        IReadOnlyList<Candidate> candidates, string query, CompletionRecency? recency = null)
    {
        var r = recency ?? NoRecency;
        return CompletionRanker.Rank(candidates, query, c => c.Text, c => c.Priority, c => r.RankOf(c.Text));
    }

    private static string Texts(CompletionRanker.Ranked<Candidate> ranked) =>
        string.Join(", ", ranked.Items.Select(i => i.Text));

    [Test]
    public async Task EmptyQuery_KeepsOrderAndSelectsHighestPriority()
    {
        Candidate[] candidates =
        [
            new("SELECT"),
            new("oi.order_id = o.id", Priority: 200),
            new("orders", Priority: 10),
        ];

        var ranked = Rank(candidates, "");

        await Assert.That(Texts(ranked)).IsEqualTo("SELECT, oi.order_id = o.id, orders");
        await Assert.That(ranked.SelectedIndex).IsEqualTo(1);
    }

    [Test]
    public async Task EmptyQuery_EmptyCandidates_SelectsNothing()
    {
        var ranked = Rank([], "");

        await Assert.That(ranked.Items).IsEmpty();
        await Assert.That(ranked.SelectedIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task Subsequence_FindsWordBoundaryMatch_NotJustPrefix()
    {
        // The motivating bug: strict prefix filtering offered only DROP for
        // "dr" and could never find daily_revenue.
        Candidate[] candidates =
        [
            new("DROP"),
            new("daily_revenue", Priority: 10),
            new("customers", Priority: 10),
        ];

        var ranked = Rank(candidates, "dr");

        await Assert.That(Texts(ranked)).Contains("daily_revenue");
        await Assert.That(Texts(ranked)).Contains("DROP");
        await Assert.That(Texts(ranked)).DoesNotContain("customers");
    }

    [Test]
    public async Task NonMatches_AreDropped_AndTopItemIsSelected()
    {
        Candidate[] candidates = [new("orders"), new("customers")];

        var ranked = Rank(candidates, "ord");

        await Assert.That(Texts(ranked)).IsEqualTo("orders");
        await Assert.That(ranked.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task NothingMatches_ReturnsEmpty()
    {
        var ranked = Rank([new Candidate("orders")], "xyz");

        await Assert.That(ranked.Items).IsEmpty();
        await Assert.That(ranked.SelectedIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task PrefixTie_ShorterNameWins()
    {
        // "ord" used to preselect order_items over orders; equal score, equal
        // priority — the shorter name is at least as likely and wins.
        Candidate[] candidates =
        [
            new("order_items", Priority: 10),
            new("orders", Priority: 10),
        ];

        var ranked = Rank(candidates, "ord");

        await Assert.That(Texts(ranked)).IsEqualTo("orders, order_items");
    }

    [Test]
    public async Task Tie_HigherContextPriorityBeatsShorterName()
    {
        // The statement's own column (priority 100) must stay above a short
        // catalog name — context outranks brevity.
        Candidate[] candidates =
        [
            new("cars", Priority: 10),
            new("customer_id", Priority: 100),
        ];

        var ranked = Rank(candidates, "c");

        await Assert.That(Texts(ranked)).IsEqualTo("customer_id, cars");
    }

    [Test]
    public async Task Tie_ExactPrefixBeatsScatteredMatch()
    {
        // Same fuzzy score can happen for a prefix hit and a lucky subsequence;
        // the item that literally starts with the query must come first.
        Candidate[] candidates =
        [
            new("no_tes", Priority: 10), // "no" + word-boundary "t": scattered
            new("notes", Priority: 10),
        ];

        var rankedPrefixSecond = Rank(candidates, "not");

        await Assert.That(rankedPrefixSecond.Items[0].Text).IsEqualTo("notes");
    }

    [Test]
    public async Task Matching_IsCaseInsensitive()
    {
        var ranked = Rank([new Candidate("SELECT")], "sel");

        await Assert.That(Texts(ranked)).IsEqualTo("SELECT");
    }

    [Test]
    public async Task FullTie_RecentlyAcceptedWins()
    {
        Candidate[] candidates =
        [
            new("region", Priority: 10),
            new("rating", Priority: 10),
        ];
        var recency = new CompletionRecency();
        recency.Record("rating");

        var ranked = Rank(candidates, "r", recency);

        await Assert.That(Texts(ranked)).IsEqualTo("rating, region");
    }

    [Test]
    public async Task FullTie_NoRecency_KeepsOriginalOrder()
    {
        Candidate[] candidates =
        [
            new("region", Priority: 10),
            new("rating", Priority: 10),
        ];

        var ranked = Rank(candidates, "r");

        await Assert.That(Texts(ranked)).IsEqualTo("region, rating");
    }

    [Test]
    public async Task FkNeighbor_OutranksPlainTable_OnEqualTextMatch()
    {
        // JOIN context: an FK-adjacent table (priority 15) must beat an
        // unrelated table (10) that matches the typed text equally well.
        Candidate[] candidates =
        [
            new("order_events", Priority: 10),
            new("order_items", Priority: 15),
        ];

        var ranked = Rank(candidates, "order_");

        await Assert.That(ranked.Items[0].Text).IsEqualTo("order_items");
    }
}
