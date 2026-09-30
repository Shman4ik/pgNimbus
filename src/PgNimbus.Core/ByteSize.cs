using System.Globalization;

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
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats <paramref name="bytes"/> as e.g. "512 bytes", "1.5 KB", "2.0 GB".
    /// Bytes are shown whole (no "0.5 bytes"); everything above uses one decimal.
    /// Negative inputs are clamped to 0 — a size is never negative.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 bytes";
        }

        if (bytes < 1024)
        {
            return $"{bytes} bytes";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}");
    }
}
