using PgNimbus.Core.Monitoring;

namespace PgNimbus.Core.Tests.Monitoring;

public class TableScanUsageTests
{
    private static TableScanUsage Usage(long seq, long idx) =>
        new("public", "t", seq, 0, idx, 0, 0, 0);

    [Test]
    public async Task NullWhenNeverScanned()
    {
        await Assert.That(Usage(0, 0).IndexScanRatio).IsNull();
    }

    [Test]
    public async Task AllSequentialIsZero()
    {
        await Assert.That(Usage(10, 0).IndexScanRatio).IsEqualTo(0.0);
    }

    [Test]
    public async Task AllIndexedIsOne()
    {
        await Assert.That(Usage(0, 10).IndexScanRatio).IsEqualTo(1.0);
    }

    [Test]
    public async Task MixedIsFraction()
    {
        await Assert.That(Usage(3, 1).IndexScanRatio).IsEqualTo(0.25);
    }
}
