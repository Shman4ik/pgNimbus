using System.Runtime.CompilerServices;
using System.Text;
using PgNimbus.Core.Export;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Export;

/// <summary>
/// The streaming export is what lets a file hold more than the grid does, so
/// the two things it has to get right are: the bytes match what the in-memory
/// writers produce (one format, however the rows arrive), and it never holds
/// more than a batch — the result it writes can be larger than memory.
/// </summary>
public sealed class ResultExporterStreamingTests
{
    private static readonly string[] Columns = ["id", "name"];

    private static List<object?[]> Rows(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new object?[] { i, $"row, \"{i}\"" })];

    private static async IAsyncEnumerable<RowBatch> Batches(
        IReadOnlyList<object?[]> rows,
        int size,
        Action<int>? beforeBatch = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var index = 0;
        foreach (var chunk in rows.Chunk(size))
        {
            ct.ThrowIfCancellationRequested();
            beforeBatch?.Invoke(index++);
            await Task.Yield();
            yield return new RowBatch(chunk);
        }
    }

    private static async Task<string> Stream(ExportFormat format, IReadOnlyList<object?[]> rows, int size)
    {
        using var stream = new MemoryStream();
        await ResultExporter.WriteStreamingAsync(format, stream, Columns, Batches(rows, size), null, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Test]
    public async Task Csv_across_batches_is_byte_for_byte_the_in_memory_csv()
    {
        var rows = Rows(25);
        var expected = new StringWriter();
        ResultExporter.WriteCsv(expected, Columns, rows);

        await Assert.That(await Stream(ExportFormat.Csv, rows, size: 7)).IsEqualTo(expected.ToString());
    }

    [Test]
    public async Task Json_across_batches_is_byte_for_byte_the_in_memory_json()
    {
        var rows = Rows(25);
        using var expected = new MemoryStream();
        ResultExporter.WriteJson(expected, Columns, rows);

        await Assert.That(await Stream(ExportFormat.Json, rows, size: 7))
            .IsEqualTo(Encoding.UTF8.GetString(expected.ToArray()));
    }

    [Test]
    public async Task An_empty_result_is_a_header_or_an_empty_array()
    {
        await Assert.That(await Stream(ExportFormat.Csv, [], size: 10)).IsEqualTo("id,name\r\n");
        await Assert.That((await Stream(ExportFormat.Json, [], size: 10)).Trim()).IsEqualTo("[]");
    }

    [Test]
    [Arguments(ExportFormat.Csv)]
    [Arguments(ExportFormat.Json)]
    public async Task Each_batch_is_on_the_stream_before_the_next_is_read(ExportFormat format)
    {
        // What "memory holds one batch" means in practice: by the time the
        // source is asked for batch N, batches before it have left the writer.
        // Utf8JsonWriter in particular keeps everything until flushed. The
        // batches are big enough to fill StreamWriter's own buffer, which is
        // allowed to hold a few KB and no more.
        var rows = Rows(3000);
        var lengths = new List<long>();
        using var stream = new MemoryStream();

        await ResultExporter.WriteStreamingAsync(
            format, stream, Columns, Batches(rows, 1000, _ => lengths.Add(stream.Length)), null, CancellationToken.None);

        await Assert.That(lengths.Count).IsEqualTo(3);
        await Assert.That(lengths[1]).IsGreaterThan(lengths[0]);
        await Assert.That(lengths[2]).IsGreaterThan(lengths[1]);
    }

    [Test]
    public async Task Progress_reports_the_running_row_count_after_each_batch()
    {
        var reported = new List<long>();
        using var stream = new MemoryStream();

        var written = await ResultExporter.WriteStreamingAsync(
            ExportFormat.Csv, stream, Columns, Batches(Rows(25), 10), reported.Add, CancellationToken.None);

        await Assert.That(written).IsEqualTo(25L);
        await Assert.That(reported).IsEquivalentTo(new List<long> { 10, 20, 25 });
    }

    [Test]
    [Arguments(ExportFormat.Csv)]
    [Arguments(ExportFormat.Json)]
    public async Task Cancelling_stops_between_batches(ExportFormat format)
    {
        using var cts = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var read = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => ResultExporter.WriteStreamingAsync(
            format,
            stream,
            Columns,
            Batches(Rows(50), 10, index =>
            {
                read = index + 1;
                if (index == 1)
                {
                    cts.Cancel();
                }
            }),
            null,
            cts.Token));

        // The batch being produced when the cancel landed may still arrive, but
        // nothing after it is asked for.
        await Assert.That(read).IsLessThanOrEqualTo(2);
    }
}
