using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class CompletionRankerTests
{
    private sealed record Candidate(string Text, double Priority = 0);

    private static readonly CompletionUsage NoUsage = new();

    private static CompletionRanker.Ranked<Candidate> Rank(
        IReadOnlyList<Candidate> candidates, string query, CompletionUsage? usage = null)
    {
        var r = usage ?? NoUsage;
        return CompletionRanker.Rank(candidates, query, c => c.Text, c => c.Priority, c => r.RankOf(c.Text));
    }

    private static string Texts(CompletionRanker.Ranked<Candidate> ranked) =>
        string.Join(", ", ranked.Items.Select(i => i.Text));

    [Test]
    public async Task EmptyQuery_OrdersByPriority_AndSelectsTheFirst()
    {
        // B05: with nothing typed, the context decides the order, not the
        // order the provider's arrays happened to be built in.
        Candidate[] candidates =
        [
            new("SELECT"),
            new("oi.order_id = o.id", Priority: 200),
            new("orders", Priority: 10),
            new("customers", Priority: 10),
        ];

        var ranked = Rank(candidates, "");

        await Assert.That(Texts(ranked)).IsEqualTo("oi.order_id = o.id, orders, customers, SELECT");
        await Assert.That(ranked.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyQuery_UsedRowsLeadTheirPriority()
    {
        Candidate[] candidates = [new("orders", 10), new("customers", 10), new("products", 10)];
        var usage = new CompletionUsage();
        usage.Record("products");

        await Assert.That(Texts(Rank(candidates, "", usage))).IsEqualTo("products, orders, customers");
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
    public async Task FullTie_RecentlyAcceptedWins()
    {
        Candidate[] candidates =
        [
            new("region", Priority: 10),
            new("rating", Priority: 10),
        ];
        var usage = new CompletionUsage();
        usage.Record("rating");

        var ranked = Rank(candidates, "r", usage);

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

    [Test]
    [Arguments("NULL", "nullif", "NULL")]
    [Arguments("null", "nullif", "NULL")]
    [Arguments("DESC", "description", "DESC")]
    public async Task A_name_typed_in_full_beats_a_longer_higher_priority_one(string query, string longer, string expected)
    {
        // A05: the function (3) and the column (100) used to outrank the
        // keyword (0) on equal fuzzy score.
        Candidate[] candidates =
        [
            new(longer, Priority: 100),
            new(expected, Priority: 0),
        ];

        var ranked = Rank(candidates, query);

        await Assert.That(ranked.Items[0].Text).IsEqualTo(expected);
    }

    // --- Match tiers (second audit B01) ---

    [Test]
    [Arguments("em", "error_message", "email")] // abbreviation vs prefix
    [Arguments("is", "ivfflat_bit_support", "is_active")]
    [Arguments("up", "unit_price", "UPDATE")]
    [Arguments("gr", "gen_random_uuid", "GROUP")]
    [Arguments("as", "account_seats", "AS")]
    [Arguments("su", "seats_used", "sum")]
    public async Task A_prefix_beats_a_higher_scored_or_higher_priority_abbreviation(string query, string abbreviation, string expected)
    {
        Candidate[] candidates =
        [
            new(abbreviation, Priority: 100),
            new(expected, Priority: 0),
        ];

        await Assert.That(Rank(candidates, query).Items[0].Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Part_starts_beat_a_substring_and_a_substring_beats_a_scatter()
    {
        Candidate[] candidates =
        [
            new("cost_index", Priority: 100), // o…i: scattered only
            new("point_id", Priority: 50),    // "oi" inside a word
            new("order_items", Priority: 0),  // o(rder) i(tems)
        ];

        await Assert.That(Texts(Rank(candidates, "oi"))).IsEqualTo("order_items, point_id, cost_index");
    }

    [Test]
    [Arguments("oi", "order_items", CompletionMatchTier.PartStarts)]
    [Arguments("ordit", "order_items", CompletionMatchTier.PartStarts)]
    [Arguments("em", "error_message", CompletionMatchTier.PartStarts)]
    [Arguments("tm", "team_members", CompletionMatchTier.PartStarts)]
    [Arguments("ci", "customerId", CompletionMatchTier.PartStarts)]
    [Arguments("em", "email", CompletionMatchTier.Prefix)]
    [Arguments("EMAIL", "email", CompletionMatchTier.Exact)]
    [Arguments("ail", "email", CompletionMatchTier.Substring)]
    [Arguments("ea", "email", CompletionMatchTier.Fuzzy)]
    [Arguments("is", "ivfflat_bit_support", CompletionMatchTier.Fuzzy)]
    public async Task TierOf_classifies(string query, string name, CompletionMatchTier expected)
    {
        await Assert.That(CompletionRanker.TierOf(name, query)).IsEqualTo(expected);
    }

    [Test]
    public async Task Within_a_tier_priority_comes_before_use_and_use_before_length()
    {
        Candidate[] candidates = [new("order_items", 10), new("orders", 10), new("order_notes", 20)];
        var usage = new CompletionUsage();
        usage.Record("order_items");

        await Assert.That(Texts(Rank(candidates, "ord", usage))).IsEqualTo("order_notes, order_items, orders");
    }
}
