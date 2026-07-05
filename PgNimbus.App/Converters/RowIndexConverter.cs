using System.Globalization;
using Avalonia.Data.Converters;

namespace PgNimbus.App.Converters;

/// <summary>
/// Extracts one cell from an <c>object?[]</c> result row. The results grid
/// binds each column to the row itself (empty path) with one of these instead
/// of a reflection path like "[3]": indexer-path bindings need dynamic code,
/// which breaks under NativeAOT/trimming (IL2026/IL3050), while an empty-path
/// binding plus converter is reflection-free.
/// </summary>
public sealed class RowIndexConverter : IValueConverter
{
    private readonly int _index;

    public RowIndexConverter(int index) => _index = index;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length ? row[_index] : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
