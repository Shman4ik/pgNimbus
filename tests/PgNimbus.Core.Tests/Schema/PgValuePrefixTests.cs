using System.Collections;
using System.Collections.Specialized;
using NpgsqlTypes;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class PgValuePrefixTests
{
    [Test]
    public async Task Negative_limits_are_rejected()
    {
        await Assert.That(() => PgValueSyntax.FormatArray(Array.Empty<int>(), maxLength: -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => PgValueSyntax.FormatHstore(new OrderedDictionary(), -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => PgValueSyntax.FormatMultirange(Array.Empty<NpgsqlRange<int>>(), PgValueSyntax.InvariantText, -1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Array_prefixes_match_the_full_literal_at_every_cut()
    {
        Array[] values =
        [
            Array.Empty<string>(),
            new object?[] { null, DBNull.Value, "", "NULL", "null", true, false, 1.25m },
            new[] { "a,b", "{x}", "a b", "a\tb\nc\r", "a\\b\"c", "😀" },
            new object[] { new[] { "a\\\"b", "NULL" }, Array.Empty<int>(), new[] { 1, 2 } },
            new[,] { { "a", "b" }, { "c", "d" } },
            new string?[,,] { { { "a b", null } }, { { "", "NULL" } } },
            new[] { new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, null, [] },
            new byte[,][] { { new byte[] { 0xDE, 0xAD } }, { new byte[] { 0xBE, 0xEF } } },
        ];

        foreach (var value in values)
        {
            var full = PgValueSyntax.FormatArray(value);
            for (var limit = 0; limit <= full.Length + 1; limit++)
            {
                await Assert.That(PgValueSyntax.FormatArray(value, maxLength: limit))
                    .IsEqualTo(full[..Math.Min(limit, full.Length)]);
            }
        }
    }

    [Test]
    public async Task Hstore_prefixes_match_the_full_literal_at_every_cut()
    {
        IDictionary[] values =
        [
            new OrderedDictionary(),
            new OrderedDictionary { ["a\\\"b"] = "x\ny\t", [""] = null, ["NULL"] = DBNull.Value, ["😀"] = "z" },
        ];
        foreach (var value in values)
        {
            var full = PgValueSyntax.FormatHstore(value);
            for (var limit = 0; limit <= full.Length + 1; limit++)
            {
                await Assert.That(PgValueSyntax.FormatHstore(value, limit))
                    .IsEqualTo(full[..Math.Min(limit, full.Length)]);
            }
        }
    }

    [Test]
    [Arguments(",")]
    [Arguments(" ")]
    [Arguments("\\")]
    [Arguments("\"")]
    public async Task A_delimiter_beyond_the_cap_still_quotes_the_element(string suffix)
    {
        var value = new[] { new string('x', 10_000) + suffix };
        await Assert.That(PgValueSyntax.FormatArray(value, maxLength: 8)).IsEqualTo("{\"xxxxxx");
    }

    [Test]
    public async Task A_nested_array_stops_calling_the_element_formatter_at_the_cap()
    {
        var calls = 0;
        string Format(object value)
        {
            calls++;
            return "x";
        }

        Array value = new[] { Enumerable.Range(0, 10_000).ToArray() };
        var prefix = PgValueSyntax.FormatArray(value, Format, 257);
        await Assert.That(prefix.Length).IsEqualTo(257);
        await Assert.That(calls).IsLessThan(257);

        calls = 0;
        PgValueSyntax.FormatArray(value, Format);
        await Assert.That(calls).IsEqualTo(10_000);
    }

    [Test]
    public async Task A_multidimensional_array_stops_calling_the_element_formatter_at_the_cap()
    {
        var calls = 0;
        string Format(object value)
        {
            calls++;
            return "x";
        }

        var value = new int[100, 100];
        var prefix = PgValueSyntax.FormatArray(value, Format, 257);
        await Assert.That(prefix).IsEqualTo(PgValueSyntax.FormatArray(value, Format)[..257]);
        calls = 0;
        PgValueSyntax.FormatArray(value, Format, 257);
        await Assert.That(calls).IsLessThan(257);
    }

    [Test]
    public async Task A_bytea_element_converts_only_the_bytes_a_prefix_shows()
    {
        // A 4 MB blob in a bytea[] cell: its preview must not build 8 MB of hex
        // to keep 257 characters of it.
        var value = new[] { new byte[4_000_000] };
        PgValueSyntax.FormatArray(value, maxLength: 257);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var prefix = PgValueSyntax.FormatArray(value, maxLength: 257);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(prefix).IsEqualTo("{\"\\\\x" + new string('0', 252));
        await Assert.That(allocated).IsLessThan(64_000L);
    }

    [Test]
    public async Task Typed_multiranges_stop_formatting_bounds_at_the_cap()
    {
        var value = Enumerable.Repeat(new NpgsqlRange<int>(1, true, 3, false), 10_000).ToArray();
        var calls = 0;
        string Format(object bound)
        {
            calls++;
            return PgValueSyntax.InvariantText(bound);
        }

        var prefix = PgValueSyntax.FormatMultirange(value, Format, 257);
        await Assert.That(calls).IsLessThan(257);
        var full = PgValueSyntax.FormatMultirange(value, PgValueSyntax.InvariantText)!;
        await Assert.That(prefix).IsEqualTo(full[..257]);
    }

    [Test]
    public async Task A_multirange_that_may_hold_a_null_is_read_past_the_cap()
    {
        // A Nullable<T>[] is an array of a struct type too, but one whose
        // elements can be null, so its tail can still make the value null.
        var value = Enumerable.Repeat<NpgsqlRange<int>?>(new NpgsqlRange<int>(1, true, 3, false), 1_000)
            .Append(null)
            .ToArray();
        await Assert.That(PgValueSyntax.FormatMultirange(value, PgValueSyntax.InvariantText)).IsNull();
        await Assert.That(PgValueSyntax.FormatMultirange(value, PgValueSyntax.InvariantText, 10)).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task An_array_of_values_that_are_not_ranges_is_no_multirange_at_any_cap(int limit)
    {
        await Assert.That(PgValueSyntax.FormatMultirange(new[] { 1, 2 }, PgValueSyntax.InvariantText, limit)).IsNull();
    }

    [Test]
    public async Task Multirange_prefixes_preserve_empty_and_unbounded_ranges()
    {
        NpgsqlRange<int>[] value = [NpgsqlRange<int>.Empty, new(0, false, true, 9, false, false), new(1, true, 3, false)];
        var full = PgValueSyntax.FormatMultirange(value, PgValueSyntax.InvariantText)!;
        for (var limit = 0; limit <= full.Length + 1; limit++)
        {
            await Assert.That(PgValueSyntax.FormatMultirange(value, PgValueSyntax.InvariantText, limit))
                .IsEqualTo(full[..Math.Min(limit, full.Length)]);
        }
    }
}
