using System.Globalization;
using PgNimbus.Core.Localization;

namespace PgNimbus.Core;

/// <summary>
/// Human-readable byte counts for the size columns (schema-tree relation sizes,
/// the database-overview panel). Base-1024 units with a fixed one-decimal step
/// once past kilobytes, so "1.2 MB" and "3.4 GB" line up at a glance the way a
/// file manager shows them. Kept in Core (not the App) so it can be unit-tested
/// and shared between <see cref="Schema.SchemaService"/> and
/// <see cref="Monitoring.DatabaseStatsService"/>.
/// </summary>
public static class ByteSize
{
    // Past bytes, one unit per step of 1024. The unit words come from Strings
    // (Russian writes КБ, МБ); the number is always invariant.
    private static readonly Func<string, string>[] Units =
    [
        Strings.Core_UnitKilobytes, Strings.Core_UnitMegabytes, Strings.Core_UnitGigabytes,
        Strings.Core_UnitTerabytes, Strings.Core_UnitPetabytes,
    ];

    /// <summary>
    /// Formats <paramref name="bytes"/> as e.g. "512 bytes", "1.5 KB", "2.0 GB",
    /// with the unit in the interface language. Bytes are shown whole (no "0.5
    /// bytes"); everything above uses one decimal. Negative inputs are clamped
    /// to 0 — a size is never negative.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return Strings.Core_UnitBytes(Math.Max(0, bytes));
        }

        double value = bytes / 1024d;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return Units[unit](value.ToString("0.0", CultureInfo.InvariantCulture));
    }
}
