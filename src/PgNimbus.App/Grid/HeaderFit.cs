using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace PgNimbus.App;

/// <summary>
/// Keeps every column of a <see cref="DataGrid"/> at least as wide as its header's
/// text: <c>app:HeaderFit.Enabled="True"</c>. The monitoring and security grids size
/// their columns in pixels for the English headers, and a translated header ("Database"
/// → "База данных") was cut off by the column's edge. A fixed width stays the column's
/// width; only a header that does not fit raises the column's <c>MinWidth</c> by what
/// it lacks, so a header that fits (every English one) leaves the grid as it was.
/// <para>
/// Measured after layout rather than computed up front, because the room a header
/// gives its text depends on the template (padding, the sort glyph's column), and a
/// language switch changes the text under a grid that is already open. A column that
/// was raised goes back to its own minimum when its header changes, then is measured
/// again.
/// </para>
/// </summary>
public static class HeaderFit
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("Enabled", typeof(HeaderFit));

    /// <summary>Each raised column's own minimum and the header text it was raised for.</summary>
    private static readonly AttachedProperty<Raised?> RaisedProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, Raised?>("Raised", typeof(HeaderFit));

    private sealed record Raised(double OwnMinWidth, string Text);

    /// <summary>
    /// What a header may lack and still read whole: the last glyph's advance ends past
    /// its ink (its side bearing), so a text short of its advance by a pixel or two
    /// loses no letter. English headers on Linux ("% time", "Database") are short by
    /// up to 2 px and draw whole; a translation cut by a letter lacks far more.
    /// </summary>
    private const double Slack = 2.5;

    static HeaderFit()
    {
        EnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                grid.LayoutUpdated += (_, _) => Fit(grid);
            }
        });
    }

    public static bool GetEnabled(DataGrid grid) => grid.GetValue(EnabledProperty);

    public static void SetEnabled(DataGrid grid, bool value) => grid.SetValue(EnabledProperty, value);

    private static void Fit(DataGrid grid)
    {
        if (!grid.IsEffectivelyVisible || !GetEnabled(grid))
        {
            return;
        }

        foreach (var header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>())
        {
            if (header.Content is not string { Length: > 0 } text
                || grid.Columns.FirstOrDefault(c => ReferenceEquals(c.Header, header.Content)) is not { } column)
            {
                continue;
            }

            // A header whose text changed (a language switch) starts again from the
            // column's own minimum; the next pass raises it again if it needs to.
            if (column.GetValue(RaisedProperty) is { } raised && raised.Text != text)
            {
                column.MinWidth = raised.OwnMinWidth;
                column.ClearValue(RaisedProperty);
                continue;
            }

            if (header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == text) is not { } block
                || block.Bounds.Width <= 0 || !header.IsEffectivelyVisible)
            {
                continue;
            }

            // The room the text has is its own box: the template's content column ends
            // where the sort glyph's begins, and the text is cut at that edge.
            var room = block.Bounds.Width;

            var lacking = Natural(block) - room;
            if (lacking > Slack)
            {
                column.SetValue(RaisedProperty, column.GetValue(RaisedProperty) ?? new Raised(column.MinWidth, text));
                column.MinWidth = Math.Ceiling(column.ActualWidth + lacking);
            }
        }
    }

    /// <summary>The width the header's text takes on one line, with nothing to stop it.</summary>
    private static double Natural(TextBlock block)
    {
        using var layout = new TextLayout(
            block.Text,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            foreground: null,
            textWrapping: TextWrapping.NoWrap,
            letterSpacing: block.LetterSpacing,
            fontFeatures: block.FontFeatures);
        return layout.WidthIncludingTrailingWhitespace;
    }
}
