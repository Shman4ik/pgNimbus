using Avalonia;
using Avalonia.Media;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Localization;

/// <summary>
/// Names the CJK face a language is drawn in. Han characters are one set of code
/// points drawn differently in Simplified Chinese, Traditional Chinese and Japanese,
/// and the system fallback Avalonia asks for a character the interface or code face
/// lacks does not know the language (the app runs with InvariantGlobalization): on
/// Windows a Simplified Chinese interface came out in Microsoft JhengHei UI, the
/// Traditional face, with its full stops and commas centred in the line and
/// Taiwanese glyph forms. So while the interface language is zh-Hans, a font
/// fallback for the CJK ranges names this platform's Simplified face.
/// </summary>
/// <remarks>
/// <para>
/// A fallback, not a face added to the <c>ContentControlThemeFontFamily</c> and
/// <c>MonoFont</c> stacks: for a character missing from the first face of a stack,
/// Avalonia 12.1 asks the system for a match without trying the rest of the stack
/// in order (measured: "Segoe UI, Microsoft YaHei UI" drew 。 in Yu Gothic UI). A
/// fallback in <see cref="FontManagerOptions"/> is tried before anything else, and
/// only for characters the face in use lacks, so Latin and Cyrillic text are drawn
/// as before; with no CJK language the list is empty and nothing changes at all.
/// </para>
/// <para>
/// The font manager reads the list it was given at startup on every lookup, so the
/// list is this class's own and a change of language edits it in place. Every app
/// builder registers it (<see cref="WithLanguageFonts"/>): the app, the screenshot
/// harness, the UI tests and UiBench. kubeNimbus has no CJK translation yet; it
/// needs the same once it has one.
/// </para>
/// </remarks>
public static class LanguageFonts
{
    /// <summary>
    /// Where Han text and its punctuation live: CJK radicals, symbols and
    /// punctuation (。、「」), kana, Han ideographs and extension A; compatibility
    /// ideographs; vertical and compatibility forms; full-width forms (（），：).
    /// </summary>
    public static readonly UnicodeRange CjkRange = UnicodeRange.Parse("U+2E80-9FFF, U+F900-FAFF, U+FE10-FE1F, U+FE30-FE4F, U+FF00-FFEF");

    private static readonly List<FontFallback> Fallbacks = [];

    /// <summary>The fallbacks in force now: one for a CJK language, none otherwise.</summary>
    public static IReadOnlyList<FontFallback> Current => Fallbacks;

    /// <summary>
    /// Registers the fallback list with the font manager. Call in every app builder,
    /// beside <c>WithNimbusFonts()</c>.
    /// </summary>
    public static AppBuilder WithLanguageFonts(this AppBuilder builder) =>
        builder.With(new FontManagerOptions { FontFallbacks = Fallbacks });

    /// <summary>Follows the interface language from now on (from <c>App.Initialize</c>).</summary>
    public static void Install()
    {
        // Once per process: the UI tests make an App per test, and a handler per
        // App is what LocalizationTests.A_new_application_adds_no_language_handler
        // exists to catch.
        if (!_installed)
        {
            _installed = true;
            Localizer.LanguageChanged += Apply;
        }

        Apply();
    }

    private static bool _installed;

    /// <summary>
    /// The Simplified Chinese face on this platform, the one the system draws its own
    /// zh-Hans interface in; null for a language that needs none.
    /// </summary>
    public static string? FaceFor(string language) =>
        language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
            ? OperatingSystem.IsWindows() ? "Microsoft YaHei UI"
            : OperatingSystem.IsMacOS() ? "PingFang SC"
            : "Noto Sans CJK SC"
            : null;

    /// <summary>Brings the fallback list in line with <see cref="Localizer.Language"/>.</summary>
    public static void Apply()
    {
        Fallbacks.Clear();
        if (FaceFor(Localizer.Language) is { } face)
        {
            Fallbacks.Add(new FontFallback { FontFamily = new FontFamily(face), UnicodeRange = CjkRange });
        }
    }
}
