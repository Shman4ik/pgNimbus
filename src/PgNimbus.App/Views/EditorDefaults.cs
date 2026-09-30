using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;

namespace PgNimbus.App.Views;

/// <summary>
/// What every AvaloniaEdit editor in the app needs set the same way: the SQL
/// editor, and the cell inspector's JSON viewer and editor.
/// </summary>
internal static class EditorDefaults
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SearchPanel, object> Wired = new();

    /// <summary>
    /// Turns off AvaloniaEdit's link rendering. It is on by default and draws
    /// every URL and e-mail address as a link in pure <c>Blue</c> over whatever
    /// the highlighter said: a list of addresses in a jsonb value came out
    /// partly blue and partly string-coloured, which read as a random highlight
    /// (2026-09, reported from real use), and an address in a SQL string literal
    /// did the same. Nothing in a query editor or a value viewer is a link, and a
    /// Ctrl+click used to open the mail client.
    /// </summary>
    public static void Apply(TextEditor editor)
    {
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
    }

    /// <summary>
    /// Wires the compact find bar's named buttons (the SearchPanel ControlTheme in
    /// Theme.axaml) straight to the panel's methods. The stock template's buttons
    /// raise a RoutedCommand from a static "last focused element", which silently
    /// misses the panel, so the compact template names its buttons instead and
    /// every editor that shows the bar has to wire them. TemplateApplied builds
    /// fresh buttons each time, so this never double-subscribes.
    /// </summary>
    public static void WireSearchButtons(SearchPanel panel)
    {
        // An editor's template can be applied more than once; its panel is wired once.
        if (!Wired.TryAdd(panel, Wired))
        {
            return;
        }

        panel.TemplateApplied += (_, e) =>
        {
            Wire(e, "PART_FindPreviousButton", () => panel.FindPrevious());
            Wire(e, "PART_FindNextButton", () => panel.FindNext());
            Wire(e, "PART_CloseButton", () => panel.Close());
            Wire(e, "PART_ReplaceNextButton", () => panel.ReplaceNext());
            Wire(e, "PART_ReplaceAllButton", () => panel.ReplaceAll());
        };

        static void Wire(TemplateAppliedEventArgs e, string name, Action action)
        {
            if (e.NameScope.Find<Button>(name) is { } button)
            {
                button.Click += (_, _) => action();
            }
        }
    }

    /// <summary>The find-match wash both editors use: the accent-tinted one the bracket pair wears.</summary>
    public static IBrush SearchResultsBrush(ThemeVariant variant) =>
        new SolidColorBrush(Color.Parse(variant == ThemeVariant.Dark ? "#40569CD6" : "#332B5FBF"));
}

/// <summary>
/// The JSON colours, in one place: the <c>Json*Brush</c> resources in
/// Theme.axaml, which the inspector's tree paints with directly and which are
/// copied here into the highlighting definition the viewer and editor draw with.
/// One definition per theme, loaded once and shared by every editor.
/// </summary>
internal static class JsonSyntax
{
    private static readonly Dictionary<ThemeVariant, IHighlightingDefinition> Definitions = [];

    // The xshd's named colours and the resource each one takes.
    private static readonly (string Color, string Resource)[] Palette =
    [
        ("Property", "JsonKeyBrush"),
        ("String", "JsonStringBrush"),
        ("Number", "JsonNumberBrush"),
        ("Keyword", "JsonLiteralBrush"),
    ];

    public static IHighlightingDefinition For(ThemeVariant variant)
    {
        var key = variant == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Definitions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var stream = AssetLoader.Open(new Uri("avares://PgNimbus.App/Assets/Json.xshd"));
        using var reader = System.Xml.XmlReader.Create(stream);
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        foreach (var (name, resource) in Palette)
        {
            if (definition.GetNamedColor(name) is { } color
                && Application.Current?.TryGetResource(resource, key, out var value) == true
                && value is ISolidColorBrush brush)
            {
                color.Foreground = new SimpleHighlightingBrush(brush.Color);
            }
        }

        Definitions[key] = definition;
        return definition;
    }
}
