using System.Globalization;

namespace PgNimbus.Core;

/// <summary>
/// Human-readable durations for the slow-query shortlist, a sibling of
/// <see cref="ByteSize"/>. pg_stat_statements counts in milliseconds, and its
/// numbers span from a 0.02 ms lookup to days of accumulated time, so one fixed
/// unit reads badly at one end or the other: this picks the unit per value, with
/// three significant-ish digits, the way a profiler does.
/// </summary>
public static class DurationText
{
    public static string Format(double milliseconds)
    {
        var ms = Math.Max(0, milliseconds);
        return ms switch
        {
            < 1 => ms.ToString("0.00", CultureInfo.InvariantCulture) + " ms",
            < 10 => ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
            < 1000 => ms.ToString("0", CultureInfo.InvariantCulture) + " ms",
            < 60_000 => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s",
            < 3_600_000 => (ms / 60_000).ToString("0.0", CultureInfo.InvariantCulture) + " min",
            _ => (ms / 3_600_000).ToString("0.0", CultureInfo.InvariantCulture) + " h",
        };
    }
}
