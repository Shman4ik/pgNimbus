using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Nimbus.Ui.Converters;

/// <summary>
/// The tooltip of a <c>TextBlock.statusMessage</c> (DESIGN.md rule 21): its whole
/// text while the line is cut with an ellipsis, and nothing while it fits (a tooltip
/// repeating what is already on screen is noise). Bound as a multi-binding over the
/// text block itself, its text and its width, so it is asked again whenever either
/// changes; by then the block's layout reflects the new width.
/// </summary>
public sealed class CutTextTip : IMultiValueConverter
{
    public static readonly CutTextTip Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [TextBlock { Text: { Length: > 0 } text } block, ..] && IsCut(block) ? text : null;

    /// <summary>
    /// Whether <paramref name="block"/> is showing less than its text: a line
    /// ended in an ellipsis, or lines left out by <c>MaxLines</c>.
    /// </summary>
    public static bool IsCut(TextBlock block)
    {
        var lines = block.TextLayout.TextLines;
        return lines.Any(line => line.HasCollapsed)
            || lines.Sum(line => line.Length) < (block.Text?.Length ?? 0);
    }
}
