using System.Collections;
using System.Collections.Specialized;
using NpgsqlTypes;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class PgValuePrefixTests
{
    [Test]
    public async Task Original_formatter_signatures_remain_available()
    {
        // Method groups require the original CLR parameter lists; optional
        // parameters alone would only preserve direct source-level calls.
        Func<Array, Func<object, string?>?, string> array = PgValueSyntax.FormatArray;
        Func<IDictionary, string> hstore = PgValueSyntax.FormatHstore;
        Func<Array, Func<object, string>, string?> multirange = PgValueSyntax.FormatMultirange;
        await Assert.That(array(new[] { 1, 2 }, null)).IsEqualTo("{1,2}");
        await Assert.That(hstore(new OrderedDictionary { ["k"] = "v" })).IsEqualTo("\"k\"=>\"v\"");
        await Assert.That(multirange(new[] { new NpgsqlRange<int>(1, true, 3, false) }, PgValueSyntax.InvariantText))
            .IsEqualTo("{[1,3)}");
    }

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
            new int?[,] { { 1, null }, { 3, 4 } },
            new int?[2, 1, 2] { { { 1, null } }, { { null, 4 } } },
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
