using System.Text;
using PgNimbus.Core.Import;

namespace PgNimbus.Core.Tests.Import;

/// <summary>
/// Security audit 2026-09, finding 18: an import read the whole file and built a
/// rows × columns matrix with no bound, so a big or sparse file could take the
/// app's memory. Each cap gets a generated input just past it, and one just at it.
/// </summary>
public sealed class TabularFileParserLimitTests
{
    [Test]
    public async Task Csv_at_the_row_cap_parses_and_one_more_row_is_refused()
    {
        var atCap = new StringBuilder("n\n").Insert(2, "1\n", TabularFileParser.MaxRows).ToString();
        await Assert.That(TabularFileParser.ParseCsv(atCap).Rows.Count).IsEqualTo(TabularFileParser.MaxRows);

        await Assert.That(() => TabularFileParser.ParseCsv(atCap + "1\n")).Throws<ImportLimitException>();
    }

    [Test]
    public async Task Json_past_the_row_cap_is_refused()
    {
        var json = new StringBuilder("[");
        for (var i = 0; i <= TabularFileParser.MaxRows; i++)
        {
            json.Append(i == 0 ? "{\"a\":1}" : ",{\"a\":1}");
        }

        json.Append(']');

        await Assert.That(() => TabularFileParser.ParseJson(json.ToString())).Throws<ImportLimitException>();
    }

    [Test]
    public async Task Csv_past_the_column_cap_is_refused()
    {
        var atCap = string.Join(',', Enumerable.Range(1, TabularFileParser.MaxColumns).Select(i => $"c{i}"));
        await Assert.That(TabularFileParser.ParseCsv(atCap + "\n").Columns.Count).IsEqualTo(TabularFileParser.MaxColumns);

        await Assert.That(() => TabularFileParser.ParseCsv(atCap + ",one_more\n")).Throws<ImportLimitException>();
    }

    [Test]
    public async Task Json_whose_keys_pass_the_column_cap_is_refused()
    {
        // The audit's shape: every object has keys of its own.
        var json = "[" + string.Join(',', Enumerable.Range(0, TabularFileParser.MaxColumns + 1).Select(i => $"{{\"k{i}\":{i}}}")) + "]";

        await Assert.That(() => TabularFileParser.ParseJson(json)).Throws<ImportLimitException>();
    }

    [Test]
    public async Task A_wide_header_over_many_short_rows_is_refused_by_the_cell_cap()
    {
        // 1,000 columns × 50,001 one-field rows: under the row and column caps,
        // but padding every row to the header's width is 50,001,000 cells.
        var header = string.Join(',', Enumerable.Range(1, TabularFileParser.MaxColumns).Select(i => $"c{i}"));
        var rows = (int)(TabularFileParser.MaxCells / TabularFileParser.MaxColumns) + 1;
        var csv = new StringBuilder(header).Append('\n').Insert(header.Length + 1, "x\n", rows).ToString();

        await Assert.That(() => TabularFileParser.ParseCsv(csv)).Throws<ImportLimitException>();
    }

    [Test]
    public async Task A_file_past_the_size_cap_is_refused_before_it_is_read()
    {
        using var stream = new HugeStream(TabularFileParser.MaxFileBytes + 1);

        await Assert.That(async () => await TabularFileParser.ReadTextAsync(stream)).Throws<ImportLimitException>();
        await Assert.That(stream.BytesRead).IsEqualTo(0);
    }

    [Test]
    public async Task A_stream_of_unknown_length_stops_at_the_size_cap()
    {
        using var stream = new HugeStream(10_000, seekable: false);

        await Assert.That(async () => await TabularFileParser.ReadTextAsync(stream, maxBytes: 4_096)).Throws<ImportLimitException>();
        await Assert.That(stream.BytesRead).IsLessThanOrEqualTo(4_096 + 81_920);
    }

    [Test]
    public async Task A_file_under_the_size_cap_reads_whole_with_its_encoding()
    {
        using var stream = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("a,b\nпривет,2\n")]);

        await Assert.That(await TabularFileParser.ReadTextAsync(stream)).IsEqualTo("a,b\nпривет,2\n");
    }

    /// <summary>A stream of spaces that claims (and, read, delivers) a length it never allocates.</summary>
    private sealed class HugeStream(long length, bool seekable = true) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => seekable;

        public override bool CanWrite => false;

        public override long Length => seekable ? length : throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - BytesRead);
            buffer.AsSpan(offset, n).Fill((byte)' ');
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
