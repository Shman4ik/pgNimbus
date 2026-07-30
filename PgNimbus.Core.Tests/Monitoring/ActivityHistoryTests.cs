using PgNimbus.Core.Monitoring;

namespace PgNimbus.Core.Tests.Monitoring;

public class ActivityHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 30, 9, 41, 0, TimeSpan.Zero);

    private static ActivitySample Sample(int i) =>
        new(T0.AddSeconds(2 * i), Backends: 10 + i, Active: i, WaitingOnLock: i % 3);

    [Test]
    public async Task EmptyHistoryHasNoSamples()
    {
        var history = new ActivityHistory();

        await Assert.That(history.Count).IsEqualTo(0);
        await Assert.That(history.Samples()).IsEmpty();
        await Assert.That(history.Series(s => s.Active)).IsEmpty();
    }

    [Test]
    public async Task SamplesComeBackOldestFirst()
    {
        var history = new ActivityHistory(capacity: 5);
        for (var i = 0; i < 3; i++)
        {
            history.Record(Sample(i));
        }

        await Assert.That(history.Series(s => s.Active)).IsEquivalentTo(new double?[] { 0, 1, 2 });
    }

    [Test]
    public async Task WrapAroundKeepsTheNewestWindowInOrder()
    {
        // The wrap is where a ring buffer usually starts drawing a plausible but
        // wrong chart, so pin it: seven samples through a five-slot window must
        // leave 2..6, in that order.
        var history = new ActivityHistory(capacity: 5);
        for (var i = 0; i < 7; i++)
        {
            history.Record(Sample(i));
        }

        await Assert.That(history.Count).IsEqualTo(5);
        await Assert.That(history.Series(s => s.Active)).IsEquivalentTo(new double?[] { 2, 3, 4, 5, 6 });
        await Assert.That(history.Samples()[0].At).IsEqualTo(T0.AddSeconds(4));
        await Assert.That(history.Samples()[4].At).IsEqualTo(T0.AddSeconds(12));
    }

    [Test]
    public async Task NeverGrowsPastCapacity()
    {
        var history = new ActivityHistory(capacity: 4);
        for (var i = 0; i < 50; i++)
        {
            history.Record(Sample(i));
        }

        await Assert.That(history.Count).IsEqualTo(4);
        await Assert.That(history.Samples()).HasCount(4);
    }

    [Test]
    public async Task AFailedPollIsAGapNotAZero()
    {
        // A server that stopped answering must not draw the same shape as a
        // server that went idle — the chart breaks its line across nulls.
        var history = new ActivityHistory(capacity: 5);
        history.Record(Sample(0));
        history.RecordGap(T0.AddSeconds(2));
        history.Record(Sample(2));

        var series = history.Series(s => s.Active);
        await Assert.That(series).IsEquivalentTo(new double?[] { 0, null, 2 });
        await Assert.That(history.Samples()[1].Backends).IsNull();
        await Assert.That(history.Samples()[1].At).IsEqualTo(T0.AddSeconds(2));
    }

    [Test]
    public async Task SeriesReturnsAFreshArrayEachCall()
    {
        // The view rebinds to a new instance per poll; handing back the same
        // buffer would mean a mutation nothing notices.
        var history = new ActivityHistory(capacity: 3);
        history.Record(Sample(0));

        await Assert.That(history.Series(s => s.Active)).IsNotSameReferenceAs(history.Series(s => s.Active));
    }

    [Test]
    public async Task CapacityMustBePositive()
    {
        await Assert.That(() => new ActivityHistory(capacity: 0)).Throws<ArgumentOutOfRangeException>();
    }
}
