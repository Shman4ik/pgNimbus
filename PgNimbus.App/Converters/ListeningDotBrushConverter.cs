using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PgNimbus.App.Converters;

/// <summary>Status-dot fill for the LISTEN/NOTIFY monitor: green while listening, grey while idle.</summary>
public sealed class ListeningDotBrushConverter : IValueConverter
{
    public static readonly ListeningDotBrushConverter Instance = new();

    private static readonly SolidColorBrush Listening = new(Color.Parse("#3FB950"));
    private static readonly SolidColorBrush Idle = new(Color.Parse("#80808080"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Listening : Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
