using PgNimbus.Core;

namespace PgNimbus.Core.Tests;

public class ByteSizeTests
{
    [Test]
    [Arguments(0L, "0 bytes")]
    [Arguments(-5L, "0 bytes")]
    [Arguments(1L, "1 bytes")]
    [Arguments(512L, "512 bytes")]
    [Arguments(1023L, "1023 bytes")]
    [Arguments(1024L, "1.0 KB")]
    [Arguments(1536L, "1.5 KB")]
    [Arguments(1048576L, "1.0 MB")]
    [Arguments(1258291L, "1.2 MB")]
    [Arguments(1073741824L, "1.0 GB")]
    [Arguments(1099511627776L, "1.0 TB")]
    public async Task Formats(long bytes, string expected)
    {
        await Assert.That(ByteSize.Format(bytes)).IsEqualTo(expected);
    }

    [Test]
    public async Task UsesInvariantDecimalSeparator()
    {
        // Guard against a machine locale rendering "1,5 KB" — the size columns
        // must read the same everywhere.
        await Assert.That(ByteSize.Format(1536L)).Contains(".");
    }
}
