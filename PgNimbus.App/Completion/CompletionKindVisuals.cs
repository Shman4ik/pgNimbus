using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace PgNimbus.App.Completion;

/// <summary>
/// The glyph + color a completion row shows for its <see cref="SqlCompletionKind"/>.
/// Geometries are the shared icon resources in <c>Styles/Theme.axaml</c> (one
/// source for all app iconography), resolved once and cached — the popup only
/// exists long after the app's styles are loaded. Colors are fixed hexes chosen
/// to stay legible on both themes, same convention as the status-bar amber.
/// </summary>
internal static class CompletionKindVisuals
{
    private static readonly Dictionary<SqlCompletionKind, Geometry?> Glyphs = [];

    private static readonly IReadOnlyDictionary<SqlCompletionKind, IBrush> Brushes = new Dictionary<SqlCompletionKind, IBrush>
    {
        [SqlCompletionKind.Keyword] = Fixed("#909090"),
        [SqlCompletionKind.Function] = Fixed("#A56EDB"),
        [SqlCompletionKind.Schema] = Fixed("#D9822B"),
        [SqlCompletionKind.Table] = Fixed("#2D7FF9"),
        [SqlCompletionKind.Column] = Fixed("#2E9E63"),
        [SqlCompletionKind.Alias] = Fixed("#1CA8C4"),
        [SqlCompletionKind.Cte] = Fixed("#7B6CDF"),
        [SqlCompletionKind.JoinCondition] = Fixed("#C9A227"),
    };

    public static IBrush Brush(SqlCompletionKind kind) => Brushes[kind];

    // UI-thread only (items are built and rendered there), so a plain cache is fine.
    public static Geometry? Glyph(SqlCompletionKind kind)
    {
        if (!Glyphs.TryGetValue(kind, out var glyph))
        {
            Glyphs[kind] = glyph =
                Application.Current is { } app && app.TryGetResource(ResourceKey(kind), null, out var resource)
                    ? resource as Geometry
                    : null;
        }

        return glyph;
    }

    private static string ResourceKey(SqlCompletionKind kind) => kind switch
    {
        SqlCompletionKind.Keyword => "TagIconGeometry",
        SqlCompletionKind.Function => "FunctionIconGeometry",
        SqlCompletionKind.Schema => "DatabaseIconGeometry",
        SqlCompletionKind.Table => "TableIconGeometry",
        SqlCompletionKind.Column => "TableColumnIconGeometry",
        SqlCompletionKind.Alias => "SwapHorizontalIconGeometry",
        SqlCompletionKind.Cte => "LayersIconGeometry",
        SqlCompletionKind.JoinCondition => "LinkIconGeometry",
        _ => "TableIconGeometry",
    };

    private static ImmutableSolidColorBrush Fixed(string hex) => new(Color.Parse(hex));
}
