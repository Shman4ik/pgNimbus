using System.Globalization;
using PgNimbus.Core.Localization;

namespace PgNimbus.Core;

/// <summary>
/// Human-readable durations for the slow-query shortlist, a sibling of
/// <see cref="ByteSize"/>. pg_stat_statements counts in milliseconds, and its
/// numbers span from a 0.02 ms lookup to days of accumulated time, so one fixed
/// unit reads badly at one end or the other: this picks the unit per value, with
/// three significant-ish digits, the way a profiler does. The unit is in the
/// interface language (Russian writes мс); the number is always invariant.
/// </summary>
public static class DurationText
{
    public static string Format(double milliseconds) => Format(
        milliseconds, Strings.Core_UnitMilliseconds, Strings.Core_UnitSeconds, Strings.Core_UnitMinutes, Strings.Core_UnitHours);

    /// <summary>
    /// The same in English, for text that is not translated: the comment a
    /// generated script opens with.
    /// </summary>
    public static string FormatEnglish(double milliseconds) => Format(
        milliseconds, static v => v + " ms", static v => v + " s", static v => v + " min", static v => v + " h");

    private static string Format(
        double milliseconds, Func<string, string> ms, Func<string, string> s, Func<string, string> min, Func<string, string> h)
    {
        var value = Math.Max(0, milliseconds);
        return value switch
        {
            < 1 => ms(value.ToString("0.00", CultureInfo.InvariantCulture)),
            < 10 => ms(value.ToString("0.0", CultureInfo.InvariantCulture)),
            < 1000 => ms(value.ToString("0", CultureInfo.InvariantCulture)),
            < 60_000 => s((value / 1000).ToString("0.0", CultureInfo.InvariantCulture)),
            < 3_600_000 => min((value / 60_000).ToString("0.0", CultureInfo.InvariantCulture)),
            _ => h((value / 3_600_000).ToString("0.0", CultureInfo.InvariantCulture)),
        };
    }
}
