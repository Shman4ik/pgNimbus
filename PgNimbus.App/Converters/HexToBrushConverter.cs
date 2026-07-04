using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PgNimbus.App.Converters;

/// <summary>Converts a "#RRGGBB" string (or null) into a brush, falling back to transparent for unset/invalid values.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
