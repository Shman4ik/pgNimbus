using PgNimbus.Core.Settings;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class CompletionUsageTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Unknown_IsMaxValue()
    {
        await Assert.That(new CompletionUsage().RankOf("orders")).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task More_accepts_rank_first_and_recency_breaks_ties()
    {
        var usage = new CompletionUsage();
        usage.Record("orders", T0);
        usage.Record("orders", T0.AddMinutes(1));
        usage.Record("customers", T0.AddMinutes(2));
        usage.Record("products", T0.AddMinutes(3));

        await Assert.That(usage.RankOf("orders")).IsEqualTo(0);
        await Assert.That(usage.RankOf("products")).IsEqualTo(1);
        await Assert.That(usage.RankOf("customers")).IsEqualTo(2);
    }

    [Test]
    public async Task Ids_are_ordinal()
    {
        var usage = new CompletionUsage();
        usage.Record("4:public:users", T0);

        await Assert.That(usage.RankOf("4:audit:users")).IsEqualTo(int.MaxValue);
        await Assert.That(usage.RankOf("4:PUBLIC:users")).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task A_full_store_forgets_the_least_recently_used()
    {
        var usage = new CompletionUsage();
        for (var i = 0; i < CompletionUsage.Capacity; i++)
        {
            usage.Record($"id{i}", T0.AddSeconds(i));
        }

        usage.Record("id0", T0.AddHours(1)); // refreshed: id1 is now the oldest
        usage.Record("new", T0.AddHours(2));

        await Assert.That(usage.Entries.Count).IsEqualTo(CompletionUsage.Capacity);
        await Assert.That(usage.RankOf("id1")).IsEqualTo(int.MaxValue);
        await Assert.That(usage.RankOf("id0")).IsEqualTo(0);
        await Assert.That(usage.RankOf("new")).IsNotEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Record_raises_changed()
    {
        var usage = new CompletionUsage();
        var raised = 0;
        usage.Changed += () => raised++;

        usage.Record("orders", T0);

        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task The_store_keeps_each_connection_apart_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-usage-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CompletionUsageStore(path);
            store.Save("db1/app", [new CompletionUsageEntry("4:public:users", 3, T0)]);
            store.Save("db2/app", [new CompletionUsageEntry("4:public:orders", 1, T0)]);

            var reread = new CompletionUsageStore(path);
            var usage = new CompletionUsage(reread.Load("db1/app"));

            await Assert.That(usage.RankOf("4:public:users")).IsEqualTo(0);
            await Assert.That(usage.RankOf("4:public:orders")).IsEqualTo(int.MaxValue);
            await Assert.That(reread.Load("db2/app").Single().Id).IsEqualTo("4:public:orders");
            await Assert.That(reread.Load("nowhere")).IsEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_corrupt_file_reads_as_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-usage-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ not json");

            await Assert.That(new CompletionUsageStore(path).Load("db/app")).IsEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
