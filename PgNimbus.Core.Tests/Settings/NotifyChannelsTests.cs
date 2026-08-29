using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Tests.Settings;

public class NotifyChannelsTests
{
    [Test]
    public async Task For_UnknownConnection_IsEmpty()
    {
        var settings = new AppSettings
        {
            NotifyChannels = { ["db1/app"] = ["order_events"] },
        };

        await Assert.That(NotifyChannels.For(settings, "db2/app")).IsEmpty();
    }

    [Test]
    public async Task For_NullConnection_IsEmpty()
    {
        // An ad-hoc connection with no host has no key to scope channels to; it
        // reads as "nothing remembered" rather than throwing.
        var settings = new AppSettings
        {
            NotifyChannels = { ["db1/app"] = ["order_events"] },
        };

        await Assert.That(NotifyChannels.For(settings, null)).IsEmpty();
    }

    [Test]
    public async Task For_ScopesToItsOwnConnection()
    {
        var settings = new AppSettings
        {
            NotifyChannels =
            {
                ["db1/app"] = ["order_events", "cache_invalidation"],
                ["db2/app"] = ["jobs"],
            },
        };

        await Assert.That(NotifyChannels.For(settings, "db1/app"))
            .IsEquivalentTo(new[] { "order_events", "cache_invalidation" });
    }

    [Test]
    public async Task With_AddsSortedAndDeduped()
    {
        var settings = new AppSettings();

        var map = NotifyChannels.With(settings, "db1/app", ["jobs", "order_events", "jobs"]);

        await Assert.That(map["db1/app"]).IsEquivalentTo(new[] { "jobs", "order_events" });
    }

    [Test]
    public async Task With_IsCaseSensitive()
    {
        // Postgres treats a quoted channel name case-sensitively, so these are
        // two channels. Folding them together would silently subscribe to one
        // and drop the other.
        var map = NotifyChannels.With(new AppSettings(), "db1/app", ["Order_Events", "order_events"]);

        await Assert.That(map["db1/app"].Count).IsEqualTo(2);
    }

    [Test]
    public async Task With_EmptyList_DropsTheEntry()
    {
        var settings = new AppSettings
        {
            NotifyChannels = { ["db1/app"] = ["order_events"] },
        };

        var map = NotifyChannels.With(settings, "db1/app", []);

        await Assert.That(map.ContainsKey("db1/app")).IsFalse();
    }

    [Test]
    public async Task With_LeavesOtherConnectionsAndTheOriginalAlone()
    {
        var settings = new AppSettings
        {
            NotifyChannels =
            {
                ["db1/app"] = ["order_events"],
                ["db2/app"] = ["jobs"],
            },
        };

        var map = NotifyChannels.With(settings, "db1/app", ["cache_invalidation"]);

        await Assert.That(map["db2/app"]).IsEquivalentTo(new[] { "jobs" });
        await Assert.That(settings.NotifyChannels["db1/app"]).IsEquivalentTo(new[] { "order_events" });
    }
}
