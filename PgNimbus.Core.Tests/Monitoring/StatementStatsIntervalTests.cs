using PgNimbus.Core.Monitoring;

namespace PgNimbus.Core.Tests.Monitoring;

/// <summary>
/// The interval is a subtraction until an entry starts over, and every way of
/// starting over has to be caught, or the shortlist shows a negative call count
/// or a whole history as one interval's work.
/// </summary>
public class StatementStatsIntervalTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    private static StatementStat Stat(long queryId, long calls, double totalMs, DateTime? since = null, long userId = 10, bool topLevel = true) =>
        new(userId, "app", queryId, topLevel, $"SELECT {queryId}", calls, totalMs, calls * 2, calls * 10, calls, since);

    private static StatementStatsSnapshot Snapshot(DateTime? reset, long? dealloc, params StatementStat[] stats) =>
        new(T0, reset, dealloc, stats, HiddenStatements: 0);

    [Test]
    public async Task A_statement_that_kept_running_contributes_only_what_it_did_in_between()
    {
        var interval = StatementStatsInterval.Between(
            Snapshot(T0, 0, Stat(1, calls: 100, totalMs: 1000)),
            Snapshot(T0, 0, Stat(1, calls: 130, totalMs: 1600)));

        var only = interval.Activity.Single();
        await Assert.That(only.Calls).IsEqualTo(30L);
        await Assert.That(only.TotalMs).IsEqualTo(600d);
        await Assert.That(only.MeanMs).IsEqualTo(20d);
        await Assert.That(only.Rows).IsEqualTo(60L);
        await Assert.That(only.Restarted).IsFalse();
        await Assert.That(interval.ViewReset).IsFalse();
    }

    [Test]
    public async Task A_statement_that_did_not_run_in_between_is_left_out()
    {
        var interval = StatementStatsInterval.Between(
            Snapshot(T0, 0, Stat(1, 100, 1000), Stat(2, 5, 50)),
            Snapshot(T0, 0, Stat(1, 100, 1000), Stat(2, 6, 60)));

        await Assert.That(interval.Activity.Select(a => a.Statement.QueryId)).IsEquivalentTo(new long[] { 2 });
    }

    [Test]
    public async Task A_new_statement_counts_in_full()
    {
        var interval = StatementStatsInterval.Between(
            Snapshot(T0, 0, Stat(1, 100, 1000)),
            Snapshot(T0, 0, Stat(1, 100, 1000), Stat(2, 7, 70)));

        var added = interval.Activity.Single();
        await Assert.That(added.Statement.QueryId).IsEqualTo(2L);
        await Assert.That(added.Calls).IsEqualTo(7L);
        await Assert.That(added.Restarted).IsFalse();
    }

    [Test]
    public async Task An_entry_whose_calls_went_down_started_over_and_counts_from_then()
    {
        // Evicted and back, on a server that doesn't report stats_since: the only
        // sign is a counter that went backwards. 100 -> 4 is 4 calls, not -96.
        var interval = StatementStatsInterval.Between(
            Snapshot(T0, 0, Stat(1, 100, 1000)),
            Snapshot(T0, 1, Stat(1, 4, 40)));

        var restarted = interval.Activity.Single();
        await Assert.That(restarted.Calls).IsEqualTo(4L);
        await Assert.That(restarted.TotalMs).IsEqualTo(40d);
        await Assert.That(restarted.Restarted).IsTrue();
        await Assert.That(interval.Evicted).IsEqualTo(1L);
    }

    [Test]
    public async Task An_entry_with_a_new_stats_since_started_over_even_with_more_calls()
    {
        // PostgreSQL 17 says so outright: counters that grew past the old ones
        // after a per-entry reset would otherwise read as a plain delta.
        var interval = StatementStatsInterval.Between(
            Snapshot(T0, 0, Stat(1, 100, 1000, since: T0.AddDays(-1))),
            Snapshot(T0, 0, Stat(1, 150, 900, since: T0.AddMinutes(5))));

        var restarted = interval.Activity.Single();
        await Assert.That(restarted.Calls).IsEqualTo(150L);
        await Assert.That(restarted.Restarted).IsTrue();
    }

    [Test]
    public async Task A_reset_of_the_whole_view_reports_everything_since_the_reset()
    {
        var interval = StatementStatsInterval.Between(
            Snapshot(T0, 0, Stat(1, 100, 1000), Stat(2, 50, 500)),
            Snapshot(T0.AddMinutes(10), 0, Stat(1, 3, 30), Stat(3, 0, 0)));

        await Assert.That(interval.ViewReset).IsTrue();
        await Assert.That(interval.Activity.Select(a => (a.Statement.QueryId, a.Calls))).IsEquivalentTo(new[] { (1L, 3L) });
    }

    [Test]
    public async Task The_same_statement_by_another_role_or_nested_is_a_different_entry()
    {
        var interval = StatementStatsInterval.Between(
            Snapshot(T0, 0, Stat(1, 10, 100, userId: 10), Stat(1, 10, 100, topLevel: false)),
            Snapshot(T0, 0, Stat(1, 12, 120, userId: 10), Stat(1, 10, 100, topLevel: false), Stat(1, 5, 50, userId: 11)));

        await Assert.That(interval.Activity.Select(a => (a.Statement.UserId, a.Calls)))
            .IsEquivalentTo(new[] { (10L, 2L), (11L, 5L) });
    }

    [Test]
    public async Task Ranking_orders_by_the_chosen_measure_and_shares_are_of_all_the_time()
    {
        StatementActivity[] activity =
        [
            StatementActivity.Cumulative(Stat(1, calls: 1000, totalMs: 500)),  // chatty, cheap
            StatementActivity.Cumulative(Stat(2, calls: 2, totalMs: 400)),     // rare, slow
            StatementActivity.Cumulative(Stat(3, calls: 10, totalMs: 100)),
        ];

        var byTotal = StatementStatsInterval.Rank(activity, StatementRanking.TotalTime, limit: 2);
        await Assert.That(byTotal.Select(r => r.Activity.Statement.QueryId)).IsEquivalentTo(new long[] { 1, 2 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // Of all 1000 ms, including the row the limit cut.
        await Assert.That(byTotal[0].TimeShare).IsEqualTo(0.5);

        var byMean = StatementStatsInterval.Rank(activity, StatementRanking.MeanTime, limit: 3);
        await Assert.That(byMean.Select(r => r.Activity.Statement.QueryId)).IsEquivalentTo(new long[] { 2, 3, 1 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        var byCalls = StatementStatsInterval.Rank(activity, StatementRanking.Calls, limit: 1);
        await Assert.That(byCalls.Single().Activity.Statement.QueryId).IsEqualTo(1L);
    }

    [Test]
    public async Task Cache_hit_ratio_is_hits_over_all_block_reads()
    {
        var activity = new StatementActivity(Stat(1, 1, 1), 1, 1, 1, SharedBlocksHit: 90, SharedBlocksRead: 10);
        await Assert.That(activity.CacheHitRatio).IsEqualTo(0.9);

        var none = new StatementActivity(Stat(1, 1, 1), 1, 1, 1, 0, 0);
        await Assert.That(none.CacheHitRatio).IsNull();
    }
}
