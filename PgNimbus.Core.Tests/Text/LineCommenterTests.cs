using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class LineCommenterTests
{
    [Test]
    public async Task CommentsAPlainBlock()
    {
        var result = LineCommenter.Toggle(["SELECT 1", "FROM t"]);

        await Assert.That(result).IsEquivalentTo(new[] { "-- SELECT 1", "-- FROM t" });
    }

    [Test]
    public async Task UncommentsAFullyCommentedBlock()
    {
        var result = LineCommenter.Toggle(["-- SELECT 1", "-- FROM t"]);

        await Assert.That(result).IsEquivalentTo(new[] { "SELECT 1", "FROM t" });
    }

    [Test]
    public async Task CommentsWhenOnlySomeLinesAreCommented()
    {
        var result = LineCommenter.Toggle(["-- SELECT 1", "FROM t"]);

        await Assert.That(result).IsEquivalentTo(new[] { "-- -- SELECT 1", "-- FROM t" });
    }

    [Test]
    public async Task MarkersLineUpAtTheBlocksCommonIndent()
    {
        // The deeper line keeps its extra indent — the block's left edge stays
        // straight instead of every marker sitting at its own column.
        var result = LineCommenter.Toggle(["  SELECT 1", "      FROM t"]);

        await Assert.That(result).IsEquivalentTo(new[] { "  -- SELECT 1", "  --     FROM t" });
    }

    [Test]
    public async Task BlankLinesAreLeftAloneButDontBlockUncommenting()
    {
        await Assert.That(LineCommenter.Toggle(["SELECT 1", "", "FROM t"]))
            .IsEquivalentTo(new[] { "-- SELECT 1", "", "-- FROM t" });

        await Assert.That(LineCommenter.Toggle(["-- SELECT 1", "", "-- FROM t"]))
            .IsEquivalentTo(new[] { "SELECT 1", "", "FROM t" });
    }

    [Test]
    public async Task RoundTripsExactly()
    {
        string[] original = ["  SELECT a,", "         b", "    FROM t"];

        var commented = LineCommenter.Toggle(original);
        var restored = LineCommenter.Toggle(commented);

        await Assert.That(restored).IsEquivalentTo(original);
    }

    [Test]
    public async Task UncommentsMarkersWrittenWithoutASpace()
    {
        var result = LineCommenter.Toggle(["--SELECT 1", "--FROM t"]);

        await Assert.That(result).IsEquivalentTo(new[] { "SELECT 1", "FROM t" });
    }

    [Test]
    public async Task AnAllBlankSelectionIsCommented()
    {
        // Nothing to uncomment, so the gesture must still do something.
        var result = LineCommenter.Toggle(["", "  "]);

        await Assert.That(result).IsEquivalentTo(new[] { "", "  " });
    }

    [Test]
    public async Task EmptyInputIsReturnedUnchanged()
    {
        await Assert.That(LineCommenter.Toggle([])).IsEmpty();
    }
}
