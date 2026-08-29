using System.Globalization;
using Avalonia.Data.Converters;

namespace PgNimbus.App.Converters;

/// <summary>
/// True when an incoming width (double) is at least the threshold passed as the
/// converter parameter. Used to reveal a control's label only once its container
/// is wide enough — e.g. the sidebar nav collapses to icons-only when narrow and
/// grows the "Schemas / Queries" text back when there's room.
/// </summary>
public sealed class MinWidthVisibilityConverter : IValueConverter
{
    public static readonly MinWidthVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d ? d : 0;
        var threshold = parameter is string s
            && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : 0;
        return width >= threshold;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
