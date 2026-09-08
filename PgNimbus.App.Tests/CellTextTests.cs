using System.Collections;
using PgNimbus.App.Converters;

namespace PgNimbus.App.Tests;

/// <summary>
/// The grid shows a capped, single-line form of each value (that cap is what
/// makes a jsonb-heavy table scroll at all — see <see cref="CellText"/>), while
/// the cell inspector shows the whole thing. Two properties have to hold for
/// that to be safe rather than lossy, and both are asserted here: the preview
/// never claims to be complete when it isn't, and <see cref="CellText.IsShortened"/>
/// agrees with the preview on every shape of value — it is what stops the grid
/// pre-filling an inline editor with a preview and saving it back.
/// </summary>
public class CellTextTests
{
    [Test]
    public async Task Short_text_is_shown_as_it_is()
    {
        await Assert.That(CellText.Preview("hello")).IsEqualTo("hello");
        await Assert.That(CellText.IsShortened("hello")).IsFalse();
    }

    [Test]
    public async Task A_long_value_is_capped_and_marked()
    {
        var value = new string('x', CellText.PreviewLength * 4);
        var preview = (string)CellText.Preview(value)!;

        await Assert.That(preview.Length).IsEqualTo(CellText.PreviewLength + 1);
        await Assert.That(preview).EndsWith(CellText.Ellipsis);
        await Assert.That(CellText.IsShortened(value)).IsTrue();
        // The inspector is where the rest of it lives.
        await Assert.That(CellText.Full(value)).IsEqualTo(value);
    }

    /// <summary>
    /// A cell is one line high. A value with newlines in it would otherwise
    /// stretch its whole row, so the preview folds them to spaces — which also
    /// means the cell is no longer showing the value verbatim, and inline
    /// editing it would save the folded copy.
    /// </summary>
    [Test]
    public async Task A_multiline_value_is_folded_onto_one_line()
    {
        const string value = "first\nsecond\tthird";

        await Assert.That(CellText.Preview(value)).IsEqualTo("first second third" + CellText.Ellipsis);
        await Assert.That(CellText.IsShortened(value)).IsTrue();
        await Assert.That(CellText.Full(value)).IsEqualTo(value);
    }

    [Test]
    public async Task Null_reads_as_the_placeholder()
    {
        await Assert.That(CellText.Preview(null)).IsEqualTo(CellText.NullPlaceholder);
        await Assert.That(CellText.Full(null)).IsEqualTo(CellText.NullPlaceholder);
        // Nothing to open the inspector for: the cell shows the whole value.
        await Assert.That(CellText.IsShortened(null)).IsFalse();
    }

    /// <summary>
    /// Numbers, dates and booleans are handed to the binding untouched, so the
    /// grid keeps formatting them with the current culture. Turning them into
    /// strings here would quietly change how every numeric column reads.
    /// </summary>
    [Test]
    public async Task Values_that_read_correctly_already_are_passed_through()
    {
        var timestamp = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

        await Assert.That(CellText.Preview(42)).IsEqualTo(42);
        await Assert.That(CellText.Preview(12.5m)).IsEqualTo(12.5m);
        await Assert.That(CellText.Preview(timestamp)).IsEqualTo(timestamp);
        await Assert.That(CellText.IsShortened(timestamp)).IsFalse();
    }

    /// <summary>
    /// bytea is the one type whose preview was always short — a blob can be
    /// megabytes, and rendering it as hex inline is pointless as well as slow.
    /// It has to report itself shortened for the same reason the others do.
    /// </summary>
    [Test]
    public async Task Bytea_shows_a_hex_preview_and_says_so()
    {
        var small = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var big = new byte[512];

        await Assert.That(CellText.Preview(small)).IsEqualTo("\\xDEADBEEF");
        await Assert.That(CellText.IsShortened(small)).IsFalse();

        await Assert.That((string)CellText.Preview(big)!).EndsWith(CellText.Ellipsis);
        await Assert.That(CellText.IsShortened(big)).IsTrue();
        await Assert.That(CellText.Full(big).Length).IsEqualTo(2 + (big.Length * 2));
    }

    [Test]
    public async Task Arrays_and_hstore_keep_their_postgres_literal()
    {
        var array = new[] { "a", "b" };
        var map = new Dictionary<string, string> { ["k"] = "v" };

        await Assert.That(CellText.Preview(array)).IsEqualTo("{a,b}");
        await Assert.That(CellText.Preview(map)).IsEqualTo("\"k\"=>\"v\"");
        await Assert.That(CellText.IsShortened(array)).IsFalse();
    }

    [Test]
    public async Task Bit_strings_render_most_significant_bit_first()
    {
        var bits = new BitArray([true, false, true, true]);

        await Assert.That(CellText.Preview(bits)).IsEqualTo("1011");
        await Assert.That(CellText.Full(bits)).IsEqualTo("1011");
        await Assert.That(CellText.IsShortened(bits)).IsFalse();
    }
}
