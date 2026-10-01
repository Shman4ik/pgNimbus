using System.Globalization;
using NpgsqlTypes;
using PgNimbus.App.Converters;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Tests;

/// <summary>
/// Ranges in the grid read the way psql prints them, and so do the arrays and
/// multiranges that hold them. Before this a range cell fell through to
/// <c>NpgsqlRange&lt;T&gt;.ToString</c>: the process culture's dates
/// (<c>07/22/2026 19:56:13</c> under the app's invariant globalization), the
/// fraction of a second and the zone gone. The grid pre-fills its inline editor
/// from that text and casts it back on commit, so an untouched edit of a
/// tstzrange moved it to the session's zone and dropped the fraction.
/// </summary>
public class RangeCellTextTests
{
    private static readonly DateTime From = new DateTime(2026, 7, 22, 19, 56, 13, DateTimeKind.Utc).AddTicks(5_436_130);
    private static readonly DateTime To = new(2026, 7, 25, 19, 56, 13, DateTimeKind.Utc);

    private const string PsqlTstzRange = "[\"2026-07-22 19:56:13.543613+00\",\"2026-07-25 19:56:13+00\")";

    private static T InCzech<T>(Func<T> read)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs-CZ");
        try
        {
            return read();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Test]
    public async Task A_tstzrange_cell_reads_as_psql_prints_it()
    {
        var range = new NpgsqlRange<DateTime>(From, true, To, false);

        await Assert.That(InCzech(() => CellText.Preview(range, "tstzrange"))).IsEqualTo(PsqlTstzRange);
        await Assert.That(InCzech(() => CellText.Full(range, "tstzrange"))).IsEqualTo(PsqlTstzRange);
    }

    [Test]
    public async Task Other_ranges_are_iso_and_culture_free()
    {
        await Assert.That(InCzech(() => CellText.Preview(new NpgsqlRange<DateOnly>(new(2026, 7, 20), true, new(2026, 7, 25), false), "daterange")))
            .IsEqualTo("[2026-07-20,2026-07-25)");
        await Assert.That(InCzech(() => CellText.Preview(new NpgsqlRange<decimal>(1.5m, true, 2.25m, false), "numrange")))
            .IsEqualTo("[1.5,2.25)");
        await Assert.That(InCzech(() => CellText.Preview(
                new NpgsqlRange<DateTime>(new DateTime(2026, 7, 22, 19, 56, 13), true, false, default, false, true), "tsrange")))
            .IsEqualTo("[\"2026-07-22 19:56:13\",)");
    }

    [Test]
    public async Task A_multirange_and_a_range_array_each_read_as_their_own_literal()
    {
        NpgsqlRange<DateTime>[] ranges = [new(From, true, To, false)];

        await Assert.That(CellText.Preview(ranges, "tstzmultirange")).IsEqualTo("{" + PsqlTstzRange + "}");
        await Assert.That(CellText.Preview(ranges, "tstzrange[]"))
            .IsEqualTo("{\"[\\\"2026-07-22 19:56:13.543613+00\\\",\\\"2026-07-25 19:56:13+00\\\")\"}");
    }

    [Test]
    public async Task A_timestamp_array_reads_like_a_timestamp()
    {
        // Same leak, one level down: an element went through the invariant culture.
        await Assert.That(CellText.Preview(new[] { From }, "timestamp with time zone[]"))
            .IsEqualTo("{\"2026-07-22 19:56:13.543613+00\"}");
        await Assert.That(CellText.Preview(new[] { new DateTime(2026, 7, 20) }, "date[]")).IsEqualTo("{2026-07-20}");
    }

    [Test]
    public async Task A_long_multirange_is_known_to_be_shortened()
    {
        // The inline editor is pre-filled from the preview, so the check that
        // keeps a shortened cell out of it must read the same text.
        var ranges = Enumerable.Range(0, 40).Select(i => new NpgsqlRange<int>(i * 10, true, i * 10 + 5, false)).ToArray();

        await Assert.That(CellText.Preview(ranges, "int4multirange")!.ToString()!).EndsWith(CellText.Ellipsis);
        await Assert.That(CellText.IsShortened(ranges, "int4multirange")).IsTrue();
        await Assert.That(CellText.IsShortened(new NpgsqlRange<int>(1, true, 5, false), "int4range")).IsFalse();
    }

    [Test]
    public async Task A_copy_writes_each_range_column_as_its_type_reads()
    {
        NpgsqlRange<int>[] ranges = [new(1, true, 3, false), new(5, true, 7, false)];
        object?[][] rows = [[new NpgsqlRange<DateTime>(From, true, To, false), ranges, ranges]];
        string[] columns = ["delivery_window", "slots", "pieces"];
        string?[] types = ["tstzrange", "int4multirange", "int4range[]"];

        var tsv = InCzech(() => QueryViewModel.FormatRows(QueryViewModel.CopyFormat.Tsv, "t", columns, rows, false, int.MaxValue, types));
        await Assert.That(tsv).IsEqualTo(
            "delivery_window\tslots\tpieces\n[2026-07-22T19:56:13.5436130Z,2026-07-25T19:56:13.0000000Z)\t{[1,3),[5,7)}\t[1,3);[5,7)\n");

        var insert = InCzech(() => QueryViewModel.FormatRows(QueryViewModel.CopyFormat.Insert, "t", columns, rows, false, int.MaxValue, types));
        await Assert.That(insert).IsEqualTo(
            "INSERT INTO t (delivery_window, slots, pieces) VALUES ('[2026-07-22T19:56:13.5436130Z,2026-07-25T19:56:13.0000000Z)', '{[1,3),[5,7)}', '{\"[1,3)\",\"[5,7)\"}');\n");
    }
}
