using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PgNimbus.App.Converters;

/// <summary>
/// Extracts one cell from an <c>object?[]</c> result row. The results grid
/// binds each column to the row itself (empty path) with one of these instead
/// of a reflection path like "[3]": indexer-path bindings need dynamic code,
/// which breaks under NativeAOT/trimming (IL2026/IL3050), while an empty-path
/// binding plus converter is reflection-free.
/// SQL NULL converts to a "NULL" placeholder (dimmed via
/// <see cref="NullCellOpacityConverter"/>) so it's distinguishable from an
/// empty string; MainWindow's cell-edit preparation clears the placeholder
/// out of the editor so it can't be committed back as a literal string.
/// How each value reads — and how much of a long one a cell shows — is
/// <see cref="CellText"/>'s business, shared with the cell inspector.
/// </summary>
public sealed class RowIndexConverter(int index) : IValueConverter
{
    private readonly int _index = index;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length ? CellText.Preview(row[_index]) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Renders a boolean cell as the app's own check/close icon geometry instead of
/// the literal "true"/"false" text, so a boolean column reads at a glance.
/// Drawn rather than typed: the ✓/✗ dingbats (U+2713/U+2717) are not carried by
/// one font on every platform — Inter has the check and not the cross — so the
/// pair fell back to different fonts and sat at visibly different heights in the
/// row. Two geometries from the same icon set share one box and one baseline.
/// Returns null for anything that is not a bool (SQL NULL, or a surprise value);
/// <see cref="BoolCellTextConverter"/> covers those cases with text.
/// </summary>
public sealed class BoolCellIconConverter(int index) : IValueConverter
{
    // UI-thread only (cells are generated and rendered there), so a plain
    // lazily-filled cache is fine.
    private static Geometry? _check;
    private static Geometry? _cross;
    private static bool _resolved;

    private readonly int _index = index;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length && row[_index] is bool b ? Icon(b) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Geometry? Icon(bool value)
    {
        if (!_resolved)
        {
            _resolved = true;
            _check = Find("CheckIconGeometry");
            _cross = Find("CloseIconGeometry");
        }

        return value ? _check : _cross;
    }

    private static Geometry? Find(string key) =>
        Application.Current is { } app && app.TryGetResource(key, null, out var resource)
            ? resource as Geometry
            : null;
}

/// <summary>
/// The text half of a boolean cell: the shared "NULL" placeholder for SQL NULL
/// and, defensively, the text form of a value a boolean column should never
/// hold. Empty for a real bool — <see cref="BoolCellIconConverter"/> draws that.
/// </summary>
public sealed class BoolCellTextConverter(int index) : IValueConverter
{
    private readonly int _index = index;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length
            ? row[_index] switch
            {
                null => CellText.NullPlaceholder,
                bool => string.Empty,
                var other => other.ToString(),
            }
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dims cells whose underlying value is SQL NULL, so the "NULL" placeholder reads as a marker, not data.</summary>
public sealed class NullCellOpacityConverter(int index) : IValueConverter
{
    private readonly int _index = index;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length && row[_index] is null ? 0.4 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
