namespace PgNimbus.Core.Monitoring;

/// <summary>
/// One polled snapshot of how busy the server is. Every count is nullable
/// because a <em>failed</em> poll has to be recorded as a gap rather than as
/// zero: a server that stopped answering must not draw the same shape as a
/// server that went quiet.
/// </summary>
public sealed record ActivitySample(DateTimeOffset At, int? Backends, int? Active, int? WaitingOnLock)
{
    /// <summary>A poll that produced no reading (the query failed, the connection dropped).</summary>
    public static ActivitySample Gap(DateTimeOffset at) => new(at, null, null, null);
}

/// <summary>
/// A bounded, session-only window of <see cref="ActivitySample"/>s — what turns
/// the activity view's point-in-time counts into a trend you can read a spike
/// off. It lives in Core because it is engine state with no UI dependency (a CLI
/// would want the same window), and it is <b>deliberately bounded and never
/// persisted</b>: <c>pg_stat_activity</c> has no history, so anything shown over
/// time is only what this session observed. Long-range server metrics are
/// Prometheus's job — do not grow this into a store.
/// </summary>
public sealed class ActivityHistory
{
    /// <summary>150 samples — five minutes at the activity window's 2-second cadence.</summary>
    public const int DefaultCapacity = 150;

    private readonly ActivitySample[] _samples;
    private int _next;
    private int _count;

    public ActivityHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _samples = new ActivitySample[capacity];
    }

    public int Capacity => _samples.Length;

    public int Count => _count;

    /// <summary>Appends a sample, dropping the oldest once the window is full.</summary>
    public void Record(ActivitySample sample)
    {
        _samples[_next] = sample;
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);
    }

    /// <summary>Records a poll that produced nothing — see <see cref="ActivitySample.Gap"/>.</summary>
    public void RecordGap(DateTimeOffset at) => Record(ActivitySample.Gap(at));

    /// <summary>The window, oldest first.</summary>
    public IReadOnlyList<ActivitySample> Samples()
    {
        var result = new ActivitySample[_count];
        var start = (_next - _count + _samples.Length) % _samples.Length;
        for (var i = 0; i < _count; i++)
        {
            result[i] = _samples[(start + i) % _samples.Length];
        }

        return result;
    }

    /// <summary>
    /// One field of the window as a plain array, oldest first — a fresh instance
    /// per call on purpose: a chart bound to a buffer mutated in place raises no
    /// change notification, and a few hundred doubles is cheaper than any
    /// observable-collection plumbing.
    /// </summary>
    public double?[] Series(Func<ActivitySample, int?> select)
    {
        var samples = Samples();
        var result = new double?[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            result[i] = select(samples[i]);
        }

        return result;
    }
}
