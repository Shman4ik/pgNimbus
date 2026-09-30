using System.Text;
using PgNimbus.Core.Export;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Export;

/// <summary>
/// Security audit 2026-09, finding 18: a text cell starting with <c>=</c>,
/// <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return is a formula to a
/// spreadsheet. The spreadsheet-safe option puts a <c>'</c> in front of those,
/// and only of those: numbers stay numbers, and the option is off by default.
/// </summary>
public sealed class ResultExporterSpreadsheetSafeTests
{
    private static readonly string[] Columns = ["id", "note"];

    [Test]
    [Arguments("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"http://x\")")]
    [Arguments("+1+1", "'+1+1")]
    [Arguments("-2+3", "'-2+3")]
    [Arguments("@SUM(A1)", "'@SUM(A1)")]
    [Arguments("\t=1", "'\t=1")]
    [Arguments("\r=1", "'\r=1")]
    [Arguments("plain", "plain")]
    [Arguments("a=b", "a=b")]
    [Arguments("", "")]
    public async Task Neutralize_prefixes_only_formula_starts(string input, string expected)
    {
        await Assert.That(ResultExporter.NeutralizeFormula(input)).IsEqualTo(expected);
    }

    private static readonly object?[][] Rows =
    [
        [-5, "=cmd|' /C calc'!A0"],
        [-1.5m, "@SUM(A1)"],
        [-2.25d, "normal"],
        [3L, null],
    ];

    [Test]
    public async Task Csv_prefixes_text_but_never_a_negative_number()
    {
        var writer = new StringWriter();
        ResultExporter.WriteCsv(writer, Columns, Rows, spreadsheetSafe: true);

        await Assert.That(writer.ToString()).IsEqualTo(
            "id,note\r\n" +
            "-5,'=cmd|' /C calc'!A0\r\n" +
            "-1.5,'@SUM(A1)\r\n" +
            "-2.25,normal\r\n" +
            "3,\r\n");
    }

    [Test]
    public async Task Tsv_prefixes_text_but_never_a_negative_number()
    {
        var writer = new StringWriter();
        ResultExporter.WriteTsv(writer, Columns, [[-5, "=1+1"], [7, "\tx"]], spreadsheetSafe: true);

        // The leading tab is judged before tabs collapse to spaces.
        await Assert.That(writer.ToString()).IsEqualTo("id\tnote\n-5\t'=1+1\n7\t' x\n");
    }

    [Test]
    public async Task Header_names_are_cells_too()
    {
        var writer = new StringWriter();
        ResultExporter.WriteCsv(writer, ["=evil"], [], spreadsheetSafe: true);

        await Assert.That(writer.ToString()).IsEqualTo("'=evil\r\n");
    }

    [Test]
    public async Task Off_by_default_the_data_is_untouched()
    {
        var csv = new StringWriter();
        ResultExporter.WriteCsv(csv, Columns, [[1, "=1+1"]]);
        var tsv = new StringWriter();
        ResultExporter.WriteTsv(tsv, Columns, [[1, "=1+1"]]);

        await Assert.That(csv.ToString()).IsEqualTo("id,note\r\n1,=1+1\r\n");
        await Assert.That(tsv.ToString()).IsEqualTo("id\tnote\n1\t=1+1\n");
    }

    [Test]
    public async Task Streaming_csv_applies_the_option()
    {
        using var stream = new MemoryStream();
        await ResultExporter.WriteStreamingAsync(
            ExportFormat.Csv,
            stream,
            Columns,
            new[] { new RowBatch([[-5, "=1+1"]]) }.ToAsyncEnumerable(),
            null,
            CancellationToken.None,
            spreadsheetSafe: true);

        await Assert.That(Encoding.UTF8.GetString(stream.ToArray())).IsEqualTo("id,note\r\n-5,'=1+1\r\n");
    }
}
