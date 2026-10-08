using System.Collections;
using System.Collections.Specialized;
using NpgsqlTypes;
using PgNimbus.App.Converters;

namespace PgNimbus.App.Tests;

public class CellTextPrefixTests
{
    private static async Task MatchesFull(object value, string? type = null)
    {
        var full = CellText.Full(value, type);
        await Assert.That(CellText.Preview(value, type)).IsEqualTo(CellText.Preview(full));
        await Assert.That(CellText.IsShortened(value, type)).IsEqualTo(CellText.IsShortened(full));
    }

    [Test]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    public async Task Literals_at_the_boundary_match_shortening_the_full_text(int length)
    {
        await MatchesFull(new[] { new string('x', length - 2) });
        await MatchesFull(new OrderedDictionary { ["k"] = new string('x', length - 7) });
    }

    [Test]
    public async Task Escapes_nulls_newlines_and_nested_arrays_keep_the_same_preview()
    {
        await MatchesFull(Array.Empty<string>());
        await MatchesFull(new OrderedDictionary());
        await MatchesFull(new object?[] { null, DBNull.Value, "", "NULL", true, false, "a\nb\tc\r", "a\\\"b" });
        await MatchesFull(new object[] { new[] { "a\\\"b", "NULL" }, new[] { new string('x', 500) } });
        await MatchesFull(new[,] { { "a", "b" }, { "c", new string('x', 500) } });
        await MatchesFull(new OrderedDictionary { ["a\\\"b"] = "x\ny\t", [""] = null, ["NULL"] = DBNull.Value });
        await MatchesFull(new[] { new string('x', 10_000) + "," });
        await MatchesFull(new[] { new string('\\', 500) });
        await MatchesFull(new OrderedDictionary { [new string('"', 500)] = "value" });
    }

    [Test]
    public async Task A_surrogate_pair_at_the_cut_is_not_split()
    {
        // '{' plus 254 x's puts the high surrogate at UTF-16 index 255.
        var value = new[] { new string('x', 254) + "😀tail" };
        await MatchesFull(value);
        await Assert.That(CellText.Preview(value)).IsEqualTo("{" + new string('x', 254) + CellText.Ellipsis);
        await MatchesFull(new OrderedDictionary { ["k"] = new string('x', 249) + "😀tail" });
    }

    [Test]
    public async Task Temporal_arrays_ranges_and_multiranges_keep_their_spelling()
    {
        var date = new DateTime(2026, 7, 20);
        await MatchesFull(Enumerable.Repeat(date, 40).ToArray(), "date[]");
        await MatchesFull(Enumerable.Repeat(DateTime.SpecifyKind(date, DateTimeKind.Utc), 40).ToArray(), "timestamp with time zone[]");
        var ranges = Enumerable.Repeat(new NpgsqlRange<DateTime>(date, true, date.AddDays(1), false), 40).ToArray();
        await MatchesFull(ranges, "daterange[]");
        await MatchesFull(ranges, "datemultirange");
        await MatchesFull(ranges, "tstzmultirange");
        await MatchesFull(ranges.Cast<object>().ToArray(), "datemultirange");
        await MatchesFull(new NpgsqlRange<int>(1, true, 5, false), "int4range");
    }

    // A value-type array as the app's sessions read it (nullable elements) and
    // a two-dimensional one: the grid shows the literal, which is also what the
    // inline editor is pre-filled with and casts back, so it must be the whole
    // value in Postgres's own shape, never a flattened {1,NULL,3,4}.
    [Test]
    public async Task Nullable_and_multi_dimensional_arrays_show_their_literal()
    {
        await Assert.That(CellText.Preview(new int?[] { 1, null, 3 }, "integer[]")).IsEqualTo("{1,NULL,3}");
        await Assert.That(CellText.Preview(new int?[,] { { 1, null }, { 3, 4 } }, "integer[]")).IsEqualTo("{{1,NULL},{3,4}}");
        await Assert.That(CellText.Full(new int?[,] { { 1, null }, { 3, 4 } }, "integer[]")).IsEqualTo("{{1,NULL},{3,4}}");
        await Assert.That(CellText.IsShortened(new int?[,] { { 1, null }, { 3, 4 } }, "integer[]")).IsFalse();
        await Assert.That(CellText.Preview(new DateOnly?[] { new DateOnly(2026, 7, 20), null }, "date[]")).IsEqualTo("{2026-07-20,NULL}");
        await MatchesFull(new int?[,] { { 1, null }, { 3, 4 } }, "integer[]");
        await MatchesFull(new decimal?[200, 2], "numeric[]");
    }

    [Test]
    public async Task An_invalid_multirange_tail_keeps_the_array_fallback()
    {
        object?[] invalid = [null, "not a range", new NpgsqlRange<string>("a", true, "b", false)];
        foreach (var tail in invalid)
        {
            var ranges = Enumerable.Repeat<object?>(new NpgsqlRange<int>(1, true, 3, false), 100).Append(tail).ToArray();
            await MatchesFull(ranges, "int4multirange");
            await Assert.That(((string)CellText.Preview(ranges, "int4multirange")!).StartsWith("{\"[1,3)\""))
                .IsTrue();
        }
    }

    [Test]
    public async Task Preview_and_inline_edit_guard_format_a_bounded_number_of_array_elements()
    {
        var element = new CountedText();
        var value = Enumerable.Repeat(element, 10_000).ToArray();
        CellText.Preview(value);
        await Assert.That(element.Calls).IsLessThan(257);
        element.Calls = 0;
        await Assert.That(CellText.IsShortened(value)).IsTrue();
        await Assert.That(element.Calls).IsLessThan(257);
        element.Calls = 0;
        CellText.Full(value);
        await Assert.That(element.Calls).IsEqualTo(value.Length);
    }

    [Test]
    public async Task Preview_and_inline_edit_guard_format_a_bounded_number_of_hstore_values()
    {
        var element = new CountedText();
        var map = new OrderedDictionary();
        for (var i = 0; i < 10_000; i++)
        {
            map.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture), element);
        }

        CellText.Preview(map);
        await Assert.That(element.Calls).IsLessThan(257);
        element.Calls = 0;
        await Assert.That(CellText.IsShortened(map)).IsTrue();
        await Assert.That(element.Calls).IsLessThan(257);
        element.Calls = 0;
        CellText.Full(map);
        await Assert.That(element.Calls).IsEqualTo(map.Count);
    }

    [Test]
    public async Task A_key_that_fills_the_preview_does_not_format_its_value()
    {
        var value = new CountedText();
        var map = new OrderedDictionary { [new string('x', 1_000)] = value };
        CellText.Preview(map);
        await Assert.That(CellText.IsShortened(map)).IsTrue();
        await Assert.That(value.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Escaping_a_large_value_allocates_only_the_preview()
    {
        var text = new string('\\', 1_000_000);
        object[] values = [new[] { text }, new OrderedDictionary { ["k"] = text }];
        foreach (var value in values)
        {
            // Warm up before counting; measure synchronous work on this thread.
            CellText.Preview(value);
            CellText.IsShortened(value);
            var before = GC.GetAllocatedBytesForCurrentThread();
            CellText.Preview(value);
            CellText.IsShortened(value);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            await Assert.That(allocated).IsLessThan(64_000L);
        }
    }

    private sealed class CountedText
    {
        public int Calls { get; set; }

        public override string ToString()
        {
            Calls++;
            return "x";
        }
    }
}
