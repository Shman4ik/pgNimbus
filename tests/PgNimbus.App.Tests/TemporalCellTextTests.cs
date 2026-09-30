using System.Globalization;
using PgNimbus.App.Converters;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Tests;

/// <summary>
/// Dates and times in the grid are ISO, the way Postgres prints them, whatever
/// the machine's region: a Mac set to Czech used to see US
/// <c>03/23/2026 02:03:29</c>, because the value went through the binding's
/// culture. And since the grid pre-fills its inline editor from that same text,
/// every rendering here must read back to the value it came from — the second
/// half of this file holds the edit path to that.
/// </summary>
public class TemporalCellTextTests
{
    private const string Date = "date";
    private const string Timestamp = "timestamp without time zone";
    private const string TimestampTz = "timestamp with time zone";
    private const string Time = "time without time zone";
    private const string TimeTz = "time with time zone";

    [Test]
    public async Task A_date_is_just_the_date()
    {
        await Assert.That(Text(new DateTime(2026, 3, 23), Date)).IsEqualTo("2026-03-23");
        await Assert.That(Text(new DateOnly(2026, 3, 23))).IsEqualTo("2026-03-23");
    }

    [Test]
    public async Task A_timestamp_shows_fractional_seconds_only_when_there_are_any()
    {
        await Assert.That(Text(new DateTime(2026, 3, 23, 2, 3, 29), Timestamp)).IsEqualTo("2026-03-23 02:03:29");
        await Assert.That(Text(new DateTime(2026, 3, 23, 2, 3, 29, 500), Timestamp)).IsEqualTo("2026-03-23 02:03:29.5");
        await Assert.That(Text(new DateTime(2026, 3, 23, 2, 3, 29).AddTicks(1_234_560), Timestamp)).IsEqualTo("2026-03-23 02:03:29.123456");
        // Midnight is still a timestamp: only the column's type makes it a date.
        await Assert.That(Text(new DateTime(2026, 3, 23), Timestamp)).IsEqualTo("2026-03-23 00:00:00");
    }

    /// <summary>
    /// Npgsql delivers every timestamptz as UTC, and the grid says so the way
    /// psql does in a UTC session, so nobody reads it as their local time.
    /// </summary>
    [Test]
    public async Task A_timestamptz_is_the_utc_instant_with_its_offset()
    {
        var utc = new DateTime(2026, 3, 23, 1, 3, 29, DateTimeKind.Utc);

        await Assert.That(Text(utc, TimestampTz)).IsEqualTo("2026-03-23 01:03:29+00");
        // The Kind alone is enough when the column's type isn't known.
        await Assert.That(Text(utc)).IsEqualTo("2026-03-23 01:03:29+00");
    }

    [Test]
    public async Task Times_are_clock_times()
    {
        await Assert.That(Text(new TimeSpan(0, 2, 3, 29, 250), Time)).IsEqualTo("02:03:29.25");
        await Assert.That(Text(new TimeOnly(2, 3, 29))).IsEqualTo("02:03:29");
        // Postgres allows the end of the day as 24:00:00.
        await Assert.That(Text(TimeSpan.FromHours(24), Time)).IsEqualTo("24:00:00");

        var timetz = new DateTimeOffset(1, 1, 2, 2, 3, 29, TimeSpan.FromHours(1));
        await Assert.That(Text(timetz, TimeTz)).IsEqualTo("02:03:29+01");
        await Assert.That(Text(new DateTimeOffset(2026, 3, 23, 2, 3, 29, new TimeSpan(5, 30, 0)))).IsEqualTo("2026-03-23 02:03:29+05:30");
        await Assert.That(Text(new DateTimeOffset(2026, 3, 23, 2, 3, 29, TimeSpan.FromHours(-3)))).IsEqualTo("2026-03-23 02:03:29-03");
    }

    /// <summary>An interval is a TimeSpan too, and is left exactly as it was.</summary>
    [Test]
    public async Task An_interval_is_not_mistaken_for_a_time()
    {
        var span = new TimeSpan(1, 2, 3, 4);

        await Assert.That(CellText.Preview(span, "interval")).IsEqualTo(span);
        await Assert.That(CellText.Preview(span)).IsEqualTo(span);
    }

    [Test]
    public async Task Infinities_read_as_postgres_spells_them()
    {
        await Assert.That(Text(DateTime.MaxValue, Timestamp)).IsEqualTo("infinity");
        await Assert.That(Text(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), TimestampTz)).IsEqualTo("-infinity");
        await Assert.That(Text(DateOnly.MaxValue)).IsEqualTo("infinity");
    }

    /// <summary>The finding itself: the text does not depend on the region.</summary>
    [Test]
    public async Task The_region_does_not_change_the_text()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "cs-CZ", "en-US", "ar-SA", "th-TH" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                await Assert.That(Text(new DateTime(2026, 3, 23, 2, 3, 29, 500), Timestamp)).IsEqualTo("2026-03-23 02:03:29.5");
                await Assert.That(Text(new DateTime(2026, 3, 23), Date)).IsEqualTo("2026-03-23");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // --- The inline-edit round trip -------------------------------------------

    [Test]
    public async Task A_date_reads_back_as_the_same_date()
    {
        await RoundTrips(new DateTime(2026, 3, 23), typeof(DateTime), Date);
        await RoundTrips(DateTime.MaxValue, typeof(DateTime), Date);
        await RoundTrips(new DateOnly(2026, 3, 23), typeof(DateOnly), Date);
        await RoundTrips(DateOnly.MinValue, typeof(DateOnly), Date);
    }

    /// <summary>
    /// Npgsql refuses a UTC DateTime for timestamp and anything else for
    /// timestamptz, so the Kind has to come back right, not just the ticks.
    /// </summary>
    [Test]
    public async Task A_timestamp_reads_back_with_the_kind_its_column_needs()
    {
        await RoundTrips(new DateTime(2026, 3, 23, 2, 3, 29, 500), typeof(DateTime), Timestamp);
        await RoundTrips(new DateTime(2026, 3, 23, 2, 3, 29).AddTicks(1_234_560), typeof(DateTime), Timestamp);
        await RoundTrips(new DateTime(2026, 3, 23, 1, 3, 29, DateTimeKind.Utc), typeof(DateTime), TimestampTz);
        await RoundTrips(new DateTime(2026, 3, 23, 1, 3, 29, DateTimeKind.Utc).AddTicks(1_234_560), typeof(DateTime), TimestampTz);
        await RoundTrips(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), typeof(DateTime), TimestampTz);
    }

    [Test]
    public async Task A_time_reads_back_as_the_same_time()
    {
        await RoundTrips(new TimeSpan(0, 2, 3, 29, 250), typeof(TimeSpan), Time);
        await RoundTrips(TimeSpan.FromHours(24), typeof(TimeSpan), Time);
        await RoundTrips(new TimeOnly(23, 59, 59, 999), typeof(TimeOnly), Time);

        // timetz: the date part is meaningless, the clock and offset are what count.
        var timetz = new DateTimeOffset(1, 1, 2, 2, 3, 29, 500, TimeSpan.FromHours(1));
        var text = (string)CellText.Preview(timetz, TimeTz)!;
        var parsed = (DateTimeOffset)QueryViewModel.ParseEditedText(text, typeof(DateTimeOffset), TimeTz);
        await Assert.That(parsed.TimeOfDay).IsEqualTo(timetz.TimeOfDay);
        await Assert.That(parsed.Offset).IsEqualTo(timetz.Offset);
    }

    private static string? Text(object value, string? type = null) => CellText.Preview(value, type) as string;

    private static async Task RoundTrips(object value, Type clrType, string type)
    {
        var text = (string)CellText.Preview(value, type)!;
        var parsed = QueryViewModel.ParseEditedText(text, clrType, type);

        await Assert.That(parsed).IsEqualTo(value);
        if (value is DateTime stamp)
        {
            await Assert.That(((DateTime)parsed).Kind == DateTimeKind.Utc).IsEqualTo(stamp.Kind == DateTimeKind.Utc);
        }
    }
}
