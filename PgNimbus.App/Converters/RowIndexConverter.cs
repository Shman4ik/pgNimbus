using System.Globalization;
using Avalonia.Data.Converters;

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
/// </summary>
public sealed class RowIndexConverter : IValueConverter
{
    public const string NullPlaceholder = "NULL";

    private readonly int _index;

    public RowIndexConverter(int index) => _index = index;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length ? row[_index] ?? NullPlaceholder : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dims cells whose underlying value is SQL NULL, so the "NULL" placeholder reads as a marker, not data.</summary>
public sealed class NullCellOpacityConverter : IValueConverter
{
    private readonly int _index;

    public NullCellOpacityConverter(int index) => _index = index;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length && row[_index] is null ? 0.4 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
