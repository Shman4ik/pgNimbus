namespace PgNimbus.Core.Monitoring;

/// <summary>What the shortlist ranks statements by.</summary>
public enum StatementRanking
{
    /// <summary>Total execution time: where the server's time actually went. The default.</summary>
    TotalTime,

    /// <summary>Mean time per call: the individually slow statements, however rarely they run.</summary>
    MeanTime,

    /// <summary>Number of calls: the chattiest statements.</summary>
    Calls,
}

/// <summary>
/// One statement's work over a period: either everything since its entry was
/// created, or what it did between two reads.
/// </summary>
/// <param name="Statement">The entry as of the later read (its text, role, and cumulative counters).</param>
/// <param name="Restarted">
/// The entry began counting again inside the interval (it was reset or evicted
/// and came back), so its numbers cover only part of the interval.
/// </param>
public sealed record StatementActivity(
    StatementStat Statement,
    long Calls,
    double TotalMs,
    long Rows,
    long SharedBlocksHit,
    long SharedBlocksRead,
    bool Restarted = false)
{
    public double MeanMs => Calls == 0 ? 0 : TotalMs / Calls;

    /// <summary>Share of block reads served from shared_buffers (0..1), or null when it read no blocks.</summary>
    public double? CacheHitRatio =>
        SharedBlocksHit + SharedBlocksRead == 0 ? null : (double)SharedBlocksHit / (SharedBlocksHit + SharedBlocksRead);

    /// <summary>Everything the entry has counted since it was created.</summary>
    public static StatementActivity Cumulative(StatementStat stat, bool restarted = false) =>
        new(stat, stat.Calls, stat.TotalMs, stat.Rows, stat.SharedBlocksHit, stat.SharedBlocksRead, restarted);
}

/// <summary>What changed between two reads of pg_stat_statements.</summary>
/// <param name="Activity">The statements that ran in the interval, unranked.</param>
/// <param name="ViewReset">
/// The whole view was reset inside the interval, so <paramref name="Activity"/>
/// covers only the time since that reset.
/// </param>
/// <param name="Evicted">
/// How many entries pg_stat_statements evicted inside the interval (to stay under
/// <c>pg_stat_statements.max</c>), or null when the server doesn't say. A statement
/// evicted and not seen again is missing from <paramref name="Activity"/>.
/// </param>
public sealed record StatementInterval(
    IReadOnlyList<StatementActivity> Activity,
    bool ViewReset,
    long? Evicted);

/// <summary>
/// The pure half of the slow-query shortlist (a sibling of
/// <see cref="BlockingTree"/>): turning two snapshots into the work done between
/// them, and ranking. pg_stat_statements' counters only grow, so a delta is a
/// subtraction, except when an entry starts over. That happens in three ways,
/// and each has to be caught or the result is nonsense (a negative call count,
/// or a whole history reported as one interval's work):
/// the whole view was reset (<c>stats_reset</c> moved), one entry was reset or
/// evicted and came back (<c>stats_since</c> moved, or, where the server doesn't
/// report it, its calls went <em>down</em>), or an entry is new. In all three the
/// later counters are the interval's work, as far as anyone can know.
/// </summary>
public static class StatementStatsInterval
{
    public static StatementInterval Between(StatementStatsSnapshot baseline, StatementStatsSnapshot current)
    {
        long? evicted = baseline.Deallocations is { } deallocBefore && current.Deallocations is { } deallocAfter && deallocAfter >= deallocBefore
            ? deallocAfter - deallocBefore
            : null;

        if (current.StatsReset is { } reset && reset != baseline.StatsReset)
        {
            // Everything in the current read was counted after the reset, which
            // is inside the interval; the baseline describes a history that's gone.
            return new StatementInterval(
                [.. current.Statements.Where(s => s.Calls > 0).Select(s => StatementActivity.Cumulative(s))],
                ViewReset: true,
                evicted);
        }

        var earlier = new Dictionary<(long, long, bool), StatementStat>();
        foreach (var stat in baseline.Statements)
        {
            earlier.TryAdd(stat.Key, stat);
        }

        var activity = new List<StatementActivity>();
        foreach (var stat in current.Statements)
        {
            if (!earlier.TryGetValue(stat.Key, out var before))
            {
                if (stat.Calls > 0)
                {
                    activity.Add(StatementActivity.Cumulative(stat));
                }

                continue;
            }

            var startedOver = stat.Calls < before.Calls
                || (stat.StatsSince is { } since && before.StatsSince is { } sinceBefore && since != sinceBefore);
            if (startedOver)
            {
                if (stat.Calls > 0)
                {
                    activity.Add(StatementActivity.Cumulative(stat, restarted: true));
                }

                continue;
            }

            var calls = stat.Calls - before.Calls;
            if (calls == 0)
            {
                continue;
            }

            activity.Add(new StatementActivity(
                stat,
                calls,
                Math.Max(0, stat.TotalMs - before.TotalMs),
                Math.Max(0, stat.Rows - before.Rows),
                Math.Max(0, stat.SharedBlocksHit - before.SharedBlocksHit),
                Math.Max(0, stat.SharedBlocksRead - before.SharedBlocksRead)));
        }

        return new StatementInterval(activity, ViewReset: false, evicted);
    }

    /// <summary>
    /// The top <paramref name="limit"/> by <paramref name="ranking"/>, each with its
    /// share of the total time of <em>all</em> the activity, not just of the rows
    /// shown: "this one statement is 40% of the database's time" is the number that
    /// says where to look first.
    /// </summary>
    public static IReadOnlyList<(StatementActivity Activity, double TimeShare)> Rank(
        IReadOnlyCollection<StatementActivity> activity,
        StatementRanking ranking,
        int limit)
    {
        var totalMs = activity.Sum(a => a.TotalMs);
        IOrderedEnumerable<StatementActivity> ordered = ranking switch
        {
            StatementRanking.MeanTime => activity.OrderByDescending(a => a.MeanMs).ThenByDescending(a => a.TotalMs),
            StatementRanking.Calls => activity.OrderByDescending(a => a.Calls).ThenByDescending(a => a.TotalMs),
            _ => activity.OrderByDescending(a => a.TotalMs).ThenByDescending(a => a.Calls),
        };

        return [.. ordered
            .ThenBy(a => a.Statement.QueryId)
            .Take(limit)
            .Select(a => (a, totalMs > 0 ? a.TotalMs / totalMs : 0))];
    }
}
