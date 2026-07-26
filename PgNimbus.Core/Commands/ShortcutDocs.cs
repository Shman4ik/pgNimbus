using System.Text;

namespace PgNimbus.Core.Commands;

/// <summary>
/// Renders <see cref="CommandCatalog"/> as the published keyboard-shortcut
/// reference. The generated file is checked in and verified by a test, so the
/// docs can't quietly fall behind the app the way a hand-written table would.
/// </summary>
public static class ShortcutDocs
{
    /// <summary>Path of the generated page, relative to the repository root.</summary>
    public const string RelativePath = "docs/reference/keyboard-shortcuts.md";

    private const string GeneratedBanner =
        "<!-- Generated from PgNimbus.Core.Commands.CommandCatalog by ShortcutDocs.ToMarkdown(). " +
        "Do not edit by hand — run the Core tests with PGNIMBUS_UPDATE_DOCS=1 to regenerate. -->";

    /// <summary>
    /// The full Markdown page. Both modifier schemes are rendered side by side
    /// so one page serves Windows/Linux and macOS readers.
    /// </summary>
    public static string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Keyboard shortcuts");
        sb.AppendLine();
        sb.AppendLine(GeneratedBanner);
        sb.AppendLine();
        sb.AppendLine("Every shortcut below is also discoverable in the app: press <kbd>F1</kbd> for the");
        sb.AppendLine("cheat sheet, or <kbd>Ctrl</kbd>+<kbd>K</kbd> to search commands by name.");
        sb.AppendLine();
        sb.AppendLine("The **Windows / Linux** and **macOS** columns differ only in the primary");
        sb.AppendLine("modifier. That choice follows the platform by default and can be forced either");
        sb.AppendLine("way in Preferences → Hotkey scheme.");
        sb.AppendLine();

        foreach (var (category, items) in CommandCatalog.CheatSheetSections())
        {
            sb.AppendLine($"## {CommandCatalog.CategoryTitle(category)}");
            sb.AppendLine();
            sb.AppendLine("| Action | Windows / Linux | macOS |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var item in items)
            {
                var windows = item.ShortcutLabel("Ctrl") ?? "—";
                var mac = item.ShortcutLabel("Cmd") ?? "—";
                sb.AppendLine($"| {Escape(item.DisplayName)} | {Escape(windows)} | {Escape(mac)} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Pipes would split a table cell; the catalog has none today, but a future
    // title containing one shouldn't silently corrupt the table.
    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
