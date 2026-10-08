using System.Globalization;
using Avalonia.Data.Converters;

namespace PgNimbus.App.Converters;

/// <summary>
/// The text a <c>CalendarDatePicker</c> shows and reads: ISO, <c>2026-10-08</c>,
/// the way the grid writes a date (<see cref="CellText"/>) and PostgreSQL prints
/// one with <c>DateStyle = ISO</c>, whatever the culture. A style in
/// <c>Styles/Theme.axaml</c> sets it on every picker in the app (row details,
/// the Add-row dialog, the filter editor, the grid's inline editors, the role
/// dialog's VALID UNTIL).
///
/// Without it the picker formats and parses with the current culture's short
/// date pattern, which is <c>MM/dd/yyyy</c> in the shipped app (it runs with
/// <c>InvariantGlobalization</c>) and the machine's pattern in the test host and
/// the tools, so the row-details form showed a date in a different shape from
/// the grid cell it came from.
/// </summary>
public static class IsoDate
{
    public const string Format = "yyyy-MM-dd";

    /// <summary>The picker's <c>TextConverter</c>.</summary>
    public static readonly IValueConverter Text = new TextConverter();

    public static string Write(DateTime date) => date.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a typed date: <c>2026-10-08</c>, or with the leading zeros left out
    /// (<c>2026-1-8</c>). Anything else is null, which the picker answers by
    /// putting back the date it had.
    /// </summary>
    public static DateTime? Read(string? text) =>
        DateTime.TryParseExact(text, "yyyy-M-d", CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date
            : null;

    private sealed class TextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is DateTime date ? Write(date) : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Read(value as string);
    }
}
