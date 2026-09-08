using System.Collections;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Converters;

/// <summary>
/// How a result-set value is rendered as text, in two lengths: the whole thing
/// (<see cref="Full"/>, what the cell inspector shows) and the short form the
/// grid puts in a cell (<see cref="Preview"/>). One place, because they have to
/// agree on what a value *is* — bytea as <c>\x</c>-hex, an array as a Postgres
/// literal, hstore as <c>"k"=&gt;"v"</c> — and disagree only on how much of it.
///
/// The cap is not cosmetic, it is why the grid scrolls. A DataGrid cell is a
/// plain <c>TextBlock</c> with no wrapping and no trimming, so it shapes and
/// measures every character it is handed, however far past the column's edge
/// they fall — and the grid measures each cell as its row is realized. On a wide
/// log table with jsonb payloads (see <c>scripts/demo/06_telemetry.sql</c>:
/// 37 KB per cell on average, 300 KB at the tail) that was ~450 ms of text
/// shaping per band of rows a wheel scroll brought into view, all of it spent on
/// characters clipped off the right-hand edge of a 560 px column.
///
/// Two rules keep the cap honest:
/// <list type="bullet">
/// <item>Nothing but the display reads it. Sorting, copy, export and every edit
/// path work off the raw row values, and the inspector formats them with
/// <see cref="Full"/>.</item>
/// <item>A shortened cell never opens an inline editor — the grid pre-fills that
/// editor from this same display text, so committing it would save the preview
/// over the real value. <see cref="IsShortened"/> is what the results grid asks
/// before it lets an edit begin; it opens the inspector instead.</item>
/// </list>
/// </summary>
public static class CellText
{
    /// <summary>Shown for SQL NULL, dimmed, so it reads as a marker rather than as the string "NULL".</summary>
    public const string NullPlaceholder = "NULL";

    /// <summary>
    /// The longest text a cell renders. Three times what the widest a column
    /// auto-sizes to can show (560 px is roughly 80 characters), so the ellipsis
    /// stays off-screen even in a hand-widened column — and measured to be the
    /// point where the grid's layout is back at the cost of a result set with no
    /// long values in it at all: on the telemetry demo table one scroll's worth
    /// of rows takes ~450 ms uncapped, 33 ms at 256, 25 ms at 128.
    /// </summary>
    public const int PreviewLength = 256;

    /// <summary>Bytes shown before a bytea preview is shortened — enough to read a magic number, not a whole blob.</summary>
    private const int ByteaPreviewBytes = 24;

    /// <summary>Marks a cell that is showing less than it holds (capped, or folded onto one line).</summary>
    public const string Ellipsis = "…";

    // Whitespace that would otherwise make a cell taller than its row, or wider
    // than the character count suggests.
    private static readonly char[] LineBreaks = ['\n', '\r', '\t'];

    /// <summary>
    /// The cell's display text: capped, and folded onto a single line. Values
    /// whose own <c>ToString</c> already reads correctly (numbers, dates,
    /// booleans) are passed through unchanged so the binding formats them with
    /// the current culture, exactly as it did before the cap existed.
    /// </summary>
    public static object? Preview(object? value) => value switch
    {
        null => NullPlaceholder,
        // bytea arrives as byte[]; its default ToString is the useless
        // "System.Byte[]". Show a capped \x-hex preview (the cell inspector
        // carries the full value) rather than materializing megabytes of hex
        // for a large blob inline.
        byte[] bytes => bytes.Length <= ByteaPreviewBytes
            ? "\\x" + Convert.ToHexString(bytes)
            : "\\x" + Convert.ToHexString(bytes.AsSpan(0, ByteaPreviewBytes)) + Ellipsis,
        // bit/varbit arrive as a BitArray, whose default ToString is
        // "System.Collections.BitArray". Render the bit string ("10110001",
        // most-significant bit first, matching Postgres) so it reads and, for
        // an editable table, round-trips through CAST(text AS bit(n)).
        BitArray bits => FormatBits(bits, PreviewLength),
        // Array columns render in Postgres's literal syntax ("{a,b}") instead
        // of the CLR default ("System.String[]") — readable, and editable in
        // place since the cell editor pre-fills from this text and the edit
        // pipeline casts it back server-side.
        Array array => Shorten(PgValueSyntax.FormatArray(array)),
        // hstore arrives as a Dictionary<string,string>, whose default ToString
        // is the CLR type name. Render the Postgres literal ("k"=>"v") so it
        // reads in any result set — browse mode already re-requests it as text,
        // but a hand-written SELECT gets the raw dictionary.
        IDictionary map => Shorten(PgValueSyntax.FormatHstore(map)),
        string text => Shorten(text),
        var other => other,
    };

    /// <summary>
    /// The value in full, the way the cell inspector shows it: the same
    /// renderings <see cref="Preview"/> uses, with nothing left out.
    /// </summary>
    public static string Full(object? value) => value switch
    {
        null => NullPlaceholder,
        byte[] bytes => "\\x" + Convert.ToHexString(bytes),
        BitArray bits => FormatBits(bits, bits.Count),
        Array array => PgValueSyntax.FormatArray(array),
        IDictionary map => PgValueSyntax.FormatHstore(map),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Whether the grid is showing less than the whole value — capped, or folded
    /// onto one line. The results grid asks this before beginning an inline
    /// edit, since that editor is pre-filled from the display text.
    /// </summary>
    public static bool IsShortened(object? value) => value switch
    {
        null => false,
        byte[] bytes => bytes.Length > ByteaPreviewBytes,
        BitArray bits => bits.Count > PreviewLength,
        Array array => NeedsShortening(PgValueSyntax.FormatArray(array)),
        IDictionary map => NeedsShortening(PgValueSyntax.FormatHstore(map)),
        string text => NeedsShortening(text),
        _ => false,
    };

    private static bool NeedsShortening(string text) =>
        text.Length > PreviewLength || text.AsSpan().IndexOfAny(LineBreaks) >= 0;

    // Cap first, then fold what is left onto one line. Both matter for the same
    // reason: a cell TextBlock lays out every character it is given, and one
    // carrying newlines grows the row instead of running off the column's edge.
    private static string Shorten(string text)
    {
        if (!NeedsShortening(text))
        {
            return text;
        }

        var cut = PreviewLength;
        // Never cut a surrogate pair in half: a lone surrogate renders as the
        // replacement glyph, so an emoji at the boundary would look corrupt.
        if (text.Length > cut && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        var span = text.Length > PreviewLength ? text.AsSpan(0, cut) : text.AsSpan();
        var chars = span.ToArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '\n' or '\r' or '\t')
            {
                chars[i] = ' ';
            }
        }

        return new string(chars) + Ellipsis;
    }

    private static string FormatBits(BitArray bits, int limit)
    {
        var length = Math.Min(bits.Count, limit);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = bits[i] ? '1' : '0';
        }

        return length < bits.Count ? new string(chars) + Ellipsis : new string(chars);
    }
}
