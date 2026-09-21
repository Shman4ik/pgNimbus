using Avalonia.Data.Converters;

namespace PgNimbus.App.Converters;

/// <summary>
/// Shortens a filter chip's text to a fixed length with an ellipsis. Used in
/// place of <c>TextTrimming</c>, whose layout could be computed while the chip
/// line was hidden and then render the chip blank once it showed.
/// </summary>
public static class ChipText
{
    public const int MaxLength = 48;

    public static readonly IValueConverter Shorten = new FuncValueConverter<string?, string>(text =>
        text is null ? string.Empty
        : text.Length <= MaxLength ? text
        : text[..(MaxLength - 1)].TrimEnd() + "…");
}
