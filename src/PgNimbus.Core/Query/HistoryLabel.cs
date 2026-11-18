using System.Globalization;
using System.Text;
using PgNimbus.Core.Localization;

namespace PgNimbus.Core.Query;

/// <summary>
/// The text a run-history row shows: the statement folded onto one line, and a
/// short "when · how long · what came back" line under it. The row used to print
/// the SQL with its own line breaks and indentation (a formatted query showed its
/// river as a staircase of spaces) and the raw <see cref="DateTimeOffset"/> in the
/// OS culture ("07/30/2026 09:41:00 +00:00"), which was wider than the sidebar
/// and said less than "09:41". Everything here is invariant culture, like the
/// results grid's ISO dates, so a row reads the same in every region.
/// </summary>
public static class HistoryLabel
{
    private const string Separator = " · ";

    /// <summary>
    /// The statement with every run of whitespace (line breaks, indentation)
    /// collapsed to one space. Whitespace inside a string literal is folded too:
    /// this is a preview, and the full text is one double-click away.
    /// </summary>
    public static string OneLine(string sql) => OneLine(sql, int.MaxValue);

    /// <summary>
    /// <see cref="OneLine(string)"/> cut at <paramref name="maxLength"/> characters,
    /// with an ellipsis when something was cut. A history row shows two lines of
    /// it; folded whole, a multi-megabyte script became one line the row laid out
    /// in full (a text block shapes a line before it trims it), every time the
    /// row scrolled into view.
    /// </summary>
    public static string OneLine(string sql, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(sql.Length, maxLength));
        var pendingSpace = false;
        foreach (var c in sql)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length >= maxLength)
            {
                return builder.Append('…').ToString();
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The statement for a tooltip: as written, cut after <paramref name="maxLines"/>
    /// lines or <paramref name="maxLength"/> characters, whichever comes first, with
    /// a closing line saying so. A tooltip lays out every line it holds.
    /// </summary>
    public static string Tip(string sql, int maxLines = 40, int maxLength = 4000)
    {
        var end = Math.Min(sql.Length, maxLength);
        var lines = 1;
        for (var i = 0; i < end; i++)
        {
            if (sql[i] == '\n' && ++lines > maxLines)
            {
                end = i;
                break;
            }
        }

        return end >= sql.Length ? sql : sql[..end].TrimEnd() + "\n…";
    }

    // Short names are words, so they are table entries (a language can write its own),
    // indexed by DayOfWeek (Sunday first) and by month - 1.
    private static readonly StringKey[] Weekdays =
    [
        StringKey.Sidebar_Weekday_Sun, StringKey.Sidebar_Weekday_Mon, StringKey.Sidebar_Weekday_Tue,
        StringKey.Sidebar_Weekday_Wed, StringKey.Sidebar_Weekday_Thu, StringKey.Sidebar_Weekday_Fri,
        StringKey.Sidebar_Weekday_Sat,
    ];

    private static readonly StringKey[] Months =
    [
        StringKey.Sidebar_Month_Jan, StringKey.Sidebar_Month_Feb, StringKey.Sidebar_Month_Mar,
        StringKey.Sidebar_Month_Apr, StringKey.Sidebar_Month_May, StringKey.Sidebar_Month_Jun,
        StringKey.Sidebar_Month_Jul, StringKey.Sidebar_Month_Aug, StringKey.Sidebar_Month_Sep,
        StringKey.Sidebar_Month_Oct, StringKey.Sidebar_Month_Nov, StringKey.Sidebar_Month_Dec,
    ];

    /// <summary>
    /// When the entry ran, as short as it can be said relative to
    /// <paramref name="now"/>: the time today, "Yesterday" and the time, the
    /// weekday and the time within the week, the month and day within the year,
    /// and the ISO date before that. Days are counted in <paramref name="now"/>'s
    /// offset, so pass a local <c>now</c> to get the user's calendar days.
    /// </summary>
    public static string When(DateTimeOffset executedAt, DateTimeOffset now)
    {
        var at = executedAt.ToOffset(now.Offset);
        var days = (now.Date - at.Date).Days;
        var time = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        return days switch
        {
            <= 0 => time,
            1 => Strings.Sidebar_History_YesterdayAt(time),
            < 7 => Strings.Sidebar_History_WeekdayAt(Strings.Get(Weekdays[(int)at.DayOfWeek]), time),
            _ when at.Year == now.Year => Strings.Sidebar_History_MonthDay(
                Strings.Get(Months[at.Month - 1]), at.Day.ToString(CultureInfo.InvariantCulture)),
            _ => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A duration in the unit it reads best in: "&lt;1 ms", "18 ms", "1.2 s", "2m 05s".</summary>
    public static string Elapsed(double milliseconds)
    {
        if (milliseconds < 1)
        {
            return Strings.Core_UnitMilliseconds("<1");
        }

        if (milliseconds < 1000)
        {
            return Strings.Core_UnitMilliseconds(milliseconds.ToString("F0", CultureInfo.InvariantCulture));
        }

        if (milliseconds < 60_000)
        {
            return Strings.Core_UnitSeconds((milliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture));
        }

        var span = TimeSpan.FromMilliseconds(milliseconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds:00}s");
    }

    /// <summary>
    /// The line under the statement: when, how long, the summary, and the
    /// connection — the last only when it isn't <paramref name="currentConnection"/>,
    /// since on a list that is almost all one connection it would repeat the
    /// title bar on every row.
    /// </summary>
    public static string Meta(QueryHistoryEntry entry, string? currentConnection, DateTimeOffset now)
    {
        var parts = new List<string> { When(entry.ExecutedAt, now), Elapsed(entry.ElapsedMs) };
        if (!string.IsNullOrWhiteSpace(entry.Summary))
        {
            parts.Add(entry.Summary);
        }

        if (!string.IsNullOrEmpty(entry.Connection) && entry.Connection != currentConnection)
        {
            parts.Add(entry.Connection);
        }

        return string.Join(Separator, parts);
    }

    /// <summary>
    /// The row's tooltip: the full local timestamp and, whatever the current
    /// connection, where it ran — what <see cref="Meta"/> shortens or leaves out.
    /// </summary>
    public static string Detail(QueryHistoryEntry entry, DateTimeOffset now)
    {
        var at = entry.ExecutedAt.ToOffset(now.Offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(entry.Connection)
            ? Strings.Sidebar_History_Ran(at)
            : Strings.Sidebar_History_RanOn(at, entry.Connection);
    }
}
