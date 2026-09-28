using System.Globalization;
using System.Text;

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
    public static string OneLine(string sql)
    {
        var builder = new StringBuilder(sql.Length);
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

            builder.Append(c);
        }

        return builder.ToString();
    }

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
            1 => "Yesterday " + time,
            < 7 => at.ToString("ddd ", CultureInfo.InvariantCulture) + time,
            _ when at.Year == now.Year => at.ToString("MMM d", CultureInfo.InvariantCulture),
            _ => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A duration in the unit it reads best in: "&lt;1 ms", "18 ms", "1.2 s", "2m 05s".</summary>
    public static string Elapsed(double milliseconds)
    {
        if (milliseconds < 1)
        {
            return "<1 ms";
        }

        if (milliseconds < 1000)
        {
            return milliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms";
        }

        if (milliseconds < 60_000)
        {
            return (milliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s";
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
        return string.IsNullOrEmpty(entry.Connection) ? "Ran " + at : $"Ran {at} on {entry.Connection}";
    }
}
