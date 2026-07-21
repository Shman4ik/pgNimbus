using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Converters;

/// <summary>
/// Maps a <see cref="PgTypeCategory"/> to the small monochrome icon and friendly
/// label the schema tree and results grid show next to a column's type. One icon
/// per family (not per exact type) keeps a single visual language across the
/// dozens of concrete Postgres type names. Geometries are parsed once and cached;
/// a bad path falls back to no icon rather than throwing, so a mis-typed glyph can
/// never crash column virtualization.
/// </summary>
public static class PgTypeVisuals
{
    // 24×24 Material Design Icon paths — same family/style as the icons already in
    // Theme.axaml. Categories with no natural glyph (Other) intentionally have none.
    private static readonly Dictionary<PgTypeCategory, string> Paths = new()
    {
        [PgTypeCategory.Numeric] = "M4,17V9H2V7H6V17H4M22,15C22,16.11 21.1,17 20,17H16V15H20V13H18V11H20V9H16V7H20A2,2 0 0,1 22,9V10.5A1.5,1.5 0 0,1 20.5,12A1.5,1.5 0 0,1 22,13.5V15M14,15V17H8V13C8,11.89 8.9,11 10,11H12V9H8V7H12A2,2 0 0,1 14,9V11C14,12.11 13.1,13 12,13H10V15H14Z",
        [PgTypeCategory.Text] = "M5,4V7H10.5V19H13.5V7H19V4H5Z",
        [PgTypeCategory.Boolean] = "M17,7H7A5,5 0 0,0 2,12A5,5 0 0,0 7,17H17A5,5 0 0,0 22,12A5,5 0 0,0 17,7M17,15A3,3 0 0,1 14,12A3,3 0 0,1 17,9A3,3 0 0,1 20,12A3,3 0 0,1 17,15Z",
        [PgTypeCategory.DateTime] = "M19,19H5V8H19M16,1V3H8V1H6V3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3H18V1M17,12H12V17H17V12Z",
        [PgTypeCategory.Uuid] = "M2,6H4V18H2V6M5,6H6V18H5V6M7,6H10V18H7V6M11,6H12V18H11V6M14,6H16V18H14V6M17,6H20V18H17V6M21,6H22V18H21V6Z",
        [PgTypeCategory.Json] = "M5,3H7V5H5V10A2,2 0 0,1 3,12A2,2 0 0,1 5,14V19H7V21H5C3.93,20.73 3,20.1 3,19V15.5C3,14.4 2.1,13.5 1,13.5V11.5C2.1,11.5 3,10.6 3,9.5V6C3,4.9 3.9,4 5,4V3M19,3C20.07,3.27 21,3.9 21,5V8.5C21,9.6 21.9,10.5 23,10.5V12.5C21.9,12.5 21,13.4 21,14.5V18C21,19.1 20.1,20 19,20V21H17V19H19V14A2,2 0 0,1 21,12A2,2 0 0,1 19,10V5H17V3H19M12,15A1,1 0 0,1 13,16A1,1 0 0,1 12,17A1,1 0 0,1 11,16A1,1 0 0,1 12,15M8,15A1,1 0 0,1 9,16A1,1 0 0,1 8,17A1,1 0 0,1 7,16A1,1 0 0,1 8,15M16,15A1,1 0 0,1 17,16A1,1 0 0,1 16,17A1,1 0 0,1 15,16A1,1 0 0,1 16,15Z",
        [PgTypeCategory.Network] = "M16.36,14C16.44,13.34 16.5,12.68 16.5,12C16.5,11.32 16.44,10.66 16.36,10H19.74C19.9,10.64 20,11.31 20,12C20,12.69 19.9,13.36 19.74,14M14.59,19.56C15.19,18.45 15.65,17.25 15.97,16H18.92C17.96,17.65 16.43,18.93 14.59,19.56M14.34,14H9.66C9.56,13.34 9.5,12.68 9.5,12C9.5,11.32 9.56,10.65 9.66,10H14.34C14.43,10.65 14.5,11.32 14.5,12C14.5,12.68 14.43,13.34 14.34,14M12,19.96C11.17,18.76 10.5,17.43 10.09,16H13.91C13.5,17.43 12.83,18.76 12,19.96M8,8H5.08C6.03,6.34 7.57,5.06 9.4,4.44C8.8,5.55 8.35,6.75 8,8M5.08,16H8C8.35,17.25 8.8,18.45 9.4,19.56C7.57,18.93 6.03,17.65 5.08,16M4.26,14C4.1,13.36 4,12.69 4,12C4,11.31 4.1,10.64 4.26,10H7.64C7.56,10.66 7.5,11.32 7.5,12C7.5,12.68 7.56,13.34 7.64,14M12,4.03C12.83,5.23 13.5,6.57 13.91,8H10.09C10.5,6.57 11.17,5.23 12,4.03M18.92,8H15.97C15.65,6.75 15.19,5.55 14.59,4.44C16.43,5.07 17.96,6.34 18.92,8M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z",
        [PgTypeCategory.Geometric] = "M2,2H8V4H16V2H22V8H20V16H22V22H16V20H8V22H2V16H4V8H2V2M16,18V16H18V8H16V6H8V8H6V16H8V18H16M4,4V6H6V4H4M18,4V6H20V4H18M4,18V20H6V18H4M18,18V20H20V18H18Z",
        [PgTypeCategory.Range] = "M9,11H15V8L19,12L15,16V13H9V16L5,12L9,8V11M2,20V4H4V20H2M20,20V4H22V20H20Z",
        [PgTypeCategory.Binary] = "M17,17H7V7H17M21,11V9H19V7C19,5.89 18.1,5 17,5H15V3H13V5H11V3H9V5H7C5.89,5 5,5.89 5,7V9H3V11H5V13H3V15H5V17A2,2 0 0,0 7,19H9V21H11V19H13V21H15V19H17A2,2 0 0,0 19,17V15H21V13H19V11M13,13H11V11H13M15,9H9V15H15V9Z",
        [PgTypeCategory.BitString] = "M2,10H6V14H2V10M8,10H12V14H8V10M14,10H18V14H14V10M20,10H22V14H20V10Z",
        [PgTypeCategory.Vector] = "M5,17.59L15.59,7H9V5H19V15H17V8.41L6.41,19L5,17.59Z",
        [PgTypeCategory.FullText] = "M19.31,18.9L22.39,22L21,23.39L17.88,20.32C17.19,20.75 16.37,21 15.5,21C13,21 11,19 11,16.5C11,14 13,12 15.5,12C18,12 20,14 20,16.5C20,17.38 19.75,18.21 19.31,18.9M15.5,14A2.5,2.5 0 0,0 13,16.5A2.5,2.5 0 0,0 15.5,19A2.5,2.5 0 0,0 18,16.5A2.5,2.5 0 0,0 15.5,14M3,5H21V7H3V5M3,9H9V11H3V9M3,13H9V15H3V13M3,17H9V19H3V17Z",
        [PgTypeCategory.Array] = "M15,4V6H18V18H15V20H20V4M4,4V20H9V18H6V6H9V4H4Z",
    };

    private static readonly Dictionary<PgTypeCategory, string> Labels = new()
    {
        [PgTypeCategory.Numeric] = "Numeric",
        [PgTypeCategory.Text] = "Text",
        [PgTypeCategory.Boolean] = "Boolean",
        [PgTypeCategory.DateTime] = "Date / time",
        [PgTypeCategory.Uuid] = "UUID",
        [PgTypeCategory.Json] = "JSON",
        [PgTypeCategory.Network] = "Network address",
        [PgTypeCategory.Geometric] = "Geometric",
        [PgTypeCategory.Range] = "Range",
        [PgTypeCategory.Binary] = "Binary",
        [PgTypeCategory.BitString] = "Bit string",
        [PgTypeCategory.Vector] = "Vector",
        [PgTypeCategory.FullText] = "Full-text search",
        [PgTypeCategory.Array] = "Array",
    };

    private static readonly Dictionary<PgTypeCategory, Geometry?> GeometryCache = new();

    /// <summary>The icon for a type name's category, or null when the family has no glyph (Other) or the path failed to parse.</summary>
    public static Geometry? Icon(string? typeName) => IconFor(PgTypeCategorizer.Categorize(typeName));

    /// <summary>The friendly family label for a type name's category, or empty for Other.</summary>
    public static string Label(string? typeName) => LabelFor(PgTypeCategorizer.Categorize(typeName));

    /// <summary>The friendly family label for an already-resolved category, or empty for Other.</summary>
    public static string LabelFor(PgTypeCategory category) =>
        Labels.TryGetValue(category, out var label) ? label : string.Empty;

    /// <summary>The icon for an already-resolved category, or null when the family has no glyph (Other) or the path failed to parse.</summary>
    public static Geometry? IconFor(PgTypeCategory category)
    {
        if (GeometryCache.TryGetValue(category, out var cached))
        {
            return cached;
        }

        Geometry? geometry = null;
        if (Paths.TryGetValue(category, out var path))
        {
            try
            {
                geometry = Geometry.Parse(path);
            }
            catch
            {
                geometry = null;
            }
        }

        GeometryCache[category] = geometry;
        return geometry;
    }
}

/// <summary>Binds a Postgres type-name string to its category icon (a <see cref="Geometry"/>).</summary>
public sealed class PgTypeIconConverter : IValueConverter
{
    public static readonly PgTypeIconConverter Instance = new();

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        PgTypeVisuals.Icon(value as string);

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}

/// <summary>Binds a Postgres type-name string to its friendly family label (for a tooltip).</summary>
public sealed class PgTypeLabelConverter : IValueConverter
{
    public static readonly PgTypeLabelConverter Instance = new();

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        PgTypeVisuals.Label(value as string);

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}
