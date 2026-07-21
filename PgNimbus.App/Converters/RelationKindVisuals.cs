using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Converters;

/// <summary>
/// Maps a <see cref="RelationKind"/> to the small monochrome icon and friendly
/// label the schema tree shows for a relation — the same one-icon-plus-tooltip
/// language the column type family uses (see <see cref="PgTypeVisuals"/>), so a
/// table, view, materialized view, and partitioned table each read distinctly at
/// a glance instead of relying on near-identical box glyphs. Geometries are parsed
/// once and cached; a bad path falls back to no icon rather than throwing.
/// </summary>
public static class RelationKindVisuals
{
    // 24×24 Material Design Icon paths — same family/style as the icons in Theme.axaml.
    private static readonly Dictionary<RelationKind, string> Paths = new()
    {
        // table
        [RelationKind.Table] = "M5,4H19A2,2 0 0,1 21,6V17A2,2 0 0,1 19,19H5A2,2 0 0,1 3,17V6A2,2 0 0,1 5,4M5,8V12H11V8H5M13,8V12H19V8H13M5,14V17H11V14H5M13,14V17H19V14H13Z",
        // eye — a view is a saved SELECT you look through
        [RelationKind.View] = "M12,9A3,3 0 0,1 15,12A3,3 0 0,1 12,15A3,3 0 0,1 9,12A3,3 0 0,1 12,9M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17Z",
        // grid-large — a view with its own materialized storage
        [RelationKind.MaterializedView] = "M3,3H21V21H3V3M5,5V11H11V5H5M13,5V11H19V5H13M5,13V19H11V13H5M13,13V19H19V13H13Z",
        // sitemap — a partitioned parent branching into its partitions
        [RelationKind.PartitionedTable] = "M9,2V8H11V11H5C3.89,11 3,11.89 3,13V16H1V22H7V16H5V13H11V16H9V22H15V16H13V13H19V16H17V22H23V16H21V13C21,11.89 20.1,11 19,11H13V8H15V2H9Z",
    };

    private static readonly Dictionary<RelationKind, string> Labels = new()
    {
        [RelationKind.Table] = "Table",
        [RelationKind.View] = "View",
        [RelationKind.MaterializedView] = "Materialized view",
        [RelationKind.PartitionedTable] = "Partitioned table",
    };

    private static readonly Dictionary<RelationKind, Geometry?> GeometryCache = new();

    public static string LabelFor(RelationKind kind) =>
        Labels.TryGetValue(kind, out var label) ? label : "Relation";

    public static Geometry? IconFor(RelationKind kind)
    {
        if (GeometryCache.TryGetValue(kind, out var cached))
        {
            return cached;
        }

        Geometry? geometry = null;
        if (Paths.TryGetValue(kind, out var path))
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

        GeometryCache[kind] = geometry;
        return geometry;
    }
}

/// <summary>Binds a <see cref="RelationKind"/> to its family icon (a <see cref="Geometry"/>).</summary>
public sealed class RelationKindIconConverter : IValueConverter
{
    public static readonly RelationKindIconConverter Instance = new();

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        value is RelationKind kind ? RelationKindVisuals.IconFor(kind) : null;

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}

/// <summary>Binds a <see cref="RelationKind"/> to its friendly label (for a tooltip).</summary>
public sealed class RelationKindLabelConverter : IValueConverter
{
    public static readonly RelationKindLabelConverter Instance = new();

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        value is RelationKind kind ? RelationKindVisuals.LabelFor(kind) : string.Empty;

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}
