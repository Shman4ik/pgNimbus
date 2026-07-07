using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PgNimbus.App.Converters;

/// <summary>Converts a word-wrap toggle (bool) into a <see cref="TextWrapping"/> value.</summary>
public sealed class BoolToTextWrappingConverter : IValueConverter
{
    public static readonly BoolToTextWrappingConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
