using System.Collections;
using System.Globalization;
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
    /// The cell's display text: capped, and folded onto a single line. Dates and
    /// times are written the ISO way Postgres writes them (<see cref="Temporal"/>),
    /// whatever the machine's region. Values whose own <c>ToString</c> already
    /// reads correctly (numbers, booleans) are passed through unchanged.
    /// </summary>
    /// <param name="value">The raw cell value.</param>
    /// <param name="dataTypeName">
    /// The column's type as the wire reports it ("date", "timestamp with time
    /// zone", …), when the caller knows it. Npgsql hands a <c>date</c> and a
    /// <c>timestamp</c> over as the same <see cref="DateTime"/>, and a <c>time</c>
    /// and an <c>interval</c> as the same <see cref="TimeSpan"/>, so only the
    /// column can say which one a value is.
    /// </param>
    public static object? Preview(object? value, string? dataTypeName = null) => value switch
    {
        null => NullPlaceholder,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan when Temporal(value, dataTypeName) is { } text => text,
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
        string text => Shorten(text),
        _ when Literal(value, dataTypeName) is { } literal => Shorten(literal),
        var other => other,
    };

    /// <summary>
    /// The value in full, the way the cell inspector shows it: the same
    /// renderings <see cref="Preview"/> uses, with nothing left out.
    /// </summary>
    public static string Full(object? value, string? dataTypeName = null) => value switch
    {
        null => NullPlaceholder,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan when Temporal(value, dataTypeName) is { } text => text,
        byte[] bytes => "\\x" + Convert.ToHexString(bytes),
        BitArray bits => FormatBits(bits, bits.Count),
        _ => Literal(value, dataTypeName) ?? value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Whether the grid is showing less than the whole value — capped, or folded
    /// onto one line. The results grid asks this before beginning an inline
    /// edit, since that editor is pre-filled from the display text. Pass the
    /// column's type whenever <see cref="Preview"/> got one: it decides the text
    /// of a multirange or an array of dates, and this must judge that same text.
    /// </summary>
    public static bool IsShortened(object? value, string? dataTypeName = null) => value switch
    {
        null => false,
        byte[] bytes => bytes.Length > ByteaPreviewBytes,
        BitArray bits => bits.Count > PreviewLength,
        string text => NeedsShortening(text),
        _ => Literal(value, dataTypeName) is { } literal && NeedsShortening(literal),
    };

    /// <summary>
    /// The Postgres literal of a value whose CLR form has no useful text of its
    /// own, or null for any other value:
    /// <list type="bullet">
    /// <item>an array, as <c>{a,b}</c> rather than "System.String[]" — readable,
    /// and editable in place, since the cell editor pre-fills from this text and
    /// the edit pipeline casts it back server-side. Its elements are written as
    /// their own cells would be, so a <c>timestamptz[]</c> reads like a
    /// timestamptz;</item>
    /// <item>a range, and a multirange (an array of ranges that only the
    /// column's type tells from a range array), as psql prints them:
    /// <c>["2026-07-22 19:56:13.543613+00","2026-07-25 19:56:13+00")</c>. A
    /// range's own <c>ToString</c> writes its bounds in the process culture and
    /// drops a timestamp's fraction and zone;</item>
    /// <item>hstore, which arrives as a <c>Dictionary&lt;string,string&gt;</c>, as
    /// <c>"k"=&gt;"v"</c> — browse mode already re-requests it as text, but a
    /// hand-written SELECT gets the raw dictionary.</item>
    /// </list>
    /// </summary>
    private static string? Literal(object value, string? dataTypeName)
    {
        switch (value)
        {
            case Array array when PgValueSyntax.IsMultirangeType(dataTypeName):
                return PgValueSyntax.FormatMultirange(array, RangeBound(dataTypeName))
                    ?? PgValueSyntax.FormatArray(array);
            case Array array:
                var elementType = ElementType(dataTypeName);
                return PgValueSyntax.FormatArray(
                    array, element => Temporal(element, elementType) ?? PgValueSyntax.FormatRange(element, RangeBound(elementType)));
            case IDictionary map:
                return PgValueSyntax.FormatHstore(map);
            default:
                return PgValueSyntax.FormatRange(value, RangeBound(dataTypeName));
        }
    }

    // How a range's bound is written: as a cell of its subtype would be. Npgsql
    // marks a tstzrange bound UTC, which is what gives it its +00; a daterange
    // bound read as a DateTime needs the column to say it is a date.
    private static Func<object, string> RangeBound(string? rangeType)
    {
        var boundType = rangeType is not null && rangeType.StartsWith("date", StringComparison.OrdinalIgnoreCase) ? "date" : null;
        return bound => Temporal(bound, boundType) ?? PgValueSyntax.InvariantText(bound);
    }

    // "timestamp with time zone[]" → "timestamp with time zone": an array's
    // wire name is its element's with the brackets after it.
    private static string? ElementType(string? arrayType) =>
        arrayType is not null && arrayType.EndsWith("[]", StringComparison.Ordinal) ? arrayType[..^2] : null;

    /// <summary>
    /// A date or time written the way Postgres itself prints it with
    /// <c>DateStyle = ISO</c>: <c>2026-03-23</c>, <c>2026-03-23 02:03:29</c>,
    /// <c>02:03:29.5</c>, with fractional seconds only when there are any, up to
    /// the six digits Postgres keeps. Null when the value is none of those, or is
    /// an <c>interval</c> (left as it was: Postgres's interval text is another
    /// shape again, and this change is about the dates).
    /// <para>
    /// <c>timestamptz</c> is shown in UTC with Postgres's <c>+00</c> suffix,
    /// exactly what psql prints in a session whose TimeZone is UTC. That is the
    /// instant Npgsql delivers (it converts every timestamptz to UTC and does not
    /// say which zone the session used), and the suffix is what makes it an
    /// instant rather than a wall clock someone could misread as local. It is also
    /// a valid literal both ways: Postgres accepts it on input, and
    /// <c>QueryViewModel.ConvertEditedValue</c> reads it back to the same UTC
    /// <see cref="DateTime"/> — which the inline edit needs, since the grid
    /// pre-fills its editor from this text. A <c>timetz</c> keeps its own offset,
    /// in the same style (<c>+01</c>, <c>+05:30</c>).
    /// </para>
    /// <para>
    /// Npgsql reads <c>infinity</c> and <c>-infinity</c> as the largest and
    /// smallest <see cref="DateTime"/> (and writes those back as infinities), so
    /// they show as Postgres spells them, and the edit path reads the words back.
    /// </para>
    /// </summary>
    public static string? Temporal(object value, string? dataTypeName = null)
    {
        if (PgValueSyntax.TemporalInfinity(value) is { } infinity)
        {
            return infinity;
        }

        switch (value)
        {
            case DateOnly date:
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly time:
                return FormatTime(time.Ticks);
            case DateTime stamp:
                if (IsType(dataTypeName, "date"))
                {
                    return stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                var text = stamp.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + FormatTime(stamp.TimeOfDay.Ticks);
                return stamp.Kind == DateTimeKind.Utc || IsWithTimeZone(dataTypeName) ? text + "+00" : text;
            case DateTimeOffset stamp:
                // timetz arrives as a DateTimeOffset whose date part means nothing.
                var clock = FormatTime(stamp.TimeOfDay.Ticks) + FormatOffset(stamp.Offset);
                return dataTypeName is not null && dataTypeName.StartsWith("time with", StringComparison.OrdinalIgnoreCase)
                    ? clock
                    : stamp.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + clock;
            case TimeSpan span when IsType(dataTypeName, "time") || IsType(dataTypeName, "time without time zone"):
                return FormatTime(span.Ticks);
            default:
                return null;
        }
    }

    private static bool IsType(string? dataTypeName, string type) =>
        string.Equals(dataTypeName, type, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithTimeZone(string? dataTypeName) =>
        dataTypeName is not null
        && (dataTypeName.Contains("with time zone", StringComparison.OrdinalIgnoreCase)
            || IsType(dataTypeName, "timestamptz"));

    // HH:mm:ss plus ".ffffff" with its trailing zeros dropped, only when the
    // second has a fraction. Written out by hand rather than with a format
    // string so a time of 24:00:00 (which Postgres allows) prints as 24, not
    // as a day.
    private static string FormatTime(long ticks)
    {
        var hours = ticks / TimeSpan.TicksPerHour;
        var minutes = ticks / TimeSpan.TicksPerMinute % 60;
        var seconds = ticks / TimeSpan.TicksPerSecond % 60;
        var micros = ticks % TimeSpan.TicksPerSecond / 10;
        var text = string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}");
        return micros == 0
            ? text
            : text + "." + micros.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    // +01, -03, +05:30: minutes only when there are any, as Postgres prints it.
    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var text = sign + abs.Hours.ToString("00", CultureInfo.InvariantCulture);
        return abs.Minutes == 0 ? text : text + ":" + abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

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
