using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class CompletionRecencyTests
{
    [Test]
    public async Task Unknown_IsMaxValue()
    {
        var recency = new CompletionRecency();

        await Assert.That(recency.RankOf("orders")).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task MostRecent_IsRankZero()
    {
        var recency = new CompletionRecency();
        recency.Record("orders");
        recency.Record("customers");

        await Assert.That(recency.RankOf("customers")).IsEqualTo(0);
        await Assert.That(recency.RankOf("orders")).IsEqualTo(1);
    }

    [Test]
    public async Task ReRecording_MovesToFront_WithoutDuplicating()
    {
        var recency = new CompletionRecency();
        recency.Record("orders");
        recency.Record("customers");
        recency.Record("orders");

        await Assert.That(recency.RankOf("orders")).IsEqualTo(0);
        await Assert.That(recency.RankOf("customers")).IsEqualTo(1);
    }

    [Test]
    public async Task Lookup_IsCaseInsensitive()
    {
        var recency = new CompletionRecency();
        recency.Record("SELECT");

        await Assert.That(recency.RankOf("select")).IsEqualTo(0);
    }

    [Test]
    public async Task Capacity_EvictsTheOldest()
    {
        var recency = new CompletionRecency();
        recency.Record("first");
        for (var i = 0; i < 50; i++)
        {
            recency.Record($"item{i}");
        }

        await Assert.That(recency.RankOf("first")).IsEqualTo(int.MaxValue);
        await Assert.That(recency.RankOf("item49")).IsEqualTo(0);
    }
}
