using SkiaSharp;

namespace Nimbus.Ui.Fonts;

/// <summary>
/// The installed monospace families, for the code font list each app's preferences page
/// offers beside the bundled face (DESIGN.md rule 22).
/// <para>
/// Asked of Skia directly rather than through Avalonia's <c>FontManager</c>: Avalonia
/// keeps every glyph typeface it creates for the life of the process, so testing a few
/// hundred installed families through it would hold all of them, CJK faces included,
/// for one look at a settings page. A Skia typeface here is opened, measured and
/// disposed. Read once per process, on the thread pool.
/// </para>
/// </summary>
public static class MonospaceFonts
{
    private static Task<IReadOnlyList<string>>? _installed;

    /// <summary>The installed monospace family names, sorted, without the bundled face.</summary>
    public static Task<IReadOnlyList<string>> InstalledAsync() => _installed ??= Task.Run(Scan);

    private static IReadOnlyList<string> Scan()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var manager = SKFontManager.Default;

        foreach (var family in manager.GetFontFamilies())
        {
            // A leading '.' is a macOS system-private face, '@' a Windows vertical one.
            if (string.IsNullOrWhiteSpace(family) || family[0] is '.' or '@'
                || string.Equals(family, NimbusFonts.BundledMonoName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var typeface = manager.MatchFamily(family);
                if (typeface is not null && IsMonospace(typeface))
                {
                    names.Add(family);
                }
            }
            catch (Exception)
            {
                // A font the platform cannot open is not one to offer.
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Fixed pitch by the font's own flag, or by measurement: several coding fonts leave
    /// the <c>post</c> table's flag off because a ligature or combining glyph has another
    /// width, and are still one cell per character for everything a user types.
    /// </summary>
    public static bool IsMonospace(SKTypeface typeface)
    {
        if (typeface.IsFixedPitch)
        {
            return true;
        }

        using var font = new SKFont(typeface, 16);
        var glyphs = font.GetGlyphs("iMW0.");
        if (Array.IndexOf(glyphs, (ushort)0) >= 0)
        {
            return false;
        }

        var widths = font.GetGlyphWidths(glyphs);
        return widths[0] > 0 && widths.All(width => Math.Abs(width - widths[0]) < 0.01f);
    }
}
