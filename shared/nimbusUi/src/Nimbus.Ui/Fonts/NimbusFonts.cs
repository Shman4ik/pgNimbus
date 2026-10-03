using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Nimbus.Ui.Fonts;

/// <summary>Which face the interface draws in (DESIGN.md rule 22).</summary>
public enum InterfaceFont
{
    /// <summary>The platform's own UI face: San Francisco on macOS, Segoe UI on Windows, the desktop's default on Linux. The default.</summary>
    System,

    /// <summary>Inter, bundled through <c>Avalonia.Fonts.Inter</c>: the same face on every platform. Opt-in.</summary>
    Inter,
}

/// <summary>
/// The bundled JetBrains Mono NL (SIL OFL 1.1, <c>Fonts/JetBrainsMono/OFL.txt</c>), under
/// the <c>fonts:JetBrainsMono</c> key. NL is the cut without ligatures: the regular one
/// draws <c>-&gt;&gt;</c> as an arrow and <c>!=</c> as ≠, in text that is there to be
/// read character by character and copied.
/// </summary>
public sealed class JetBrainsMonoFontCollection() : EmbeddedFontCollection(
    new Uri(NimbusFonts.MonoCollectionKey, UriKind.Absolute),
    new Uri("avares://Nimbus.Ui/Fonts/JetBrainsMono", UriKind.Absolute));

/// <summary>
/// The family's typography (DESIGN.md rule 22): one resource per role, set once per
/// app from its settings, never a font name written at a use site.
/// <list type="bullet">
/// <item><c>ContentControlThemeFontFamily</c>, Fluent's own key, which every
/// <c>Window</c>, popup and overlay host reads: the interface face.</item>
/// <item><c>MonoFont</c>: code, values, identifiers, through the <c>mono</c> class.</item>
/// <item><c>KeyCapFont</c>: text that spells a shortcut (rule 4).</item>
/// <item><c>InterfaceLetterSpacing</c>: the compensation <see cref="InterfaceLetterSpacing"/>
/// explains, applied to every top level.</item>
/// </list>
/// </summary>
public static class NimbusFonts
{
    /// <summary>The bundled monospace collection's key.</summary>
    public const string MonoCollectionKey = "fonts:JetBrainsMono";

    /// <summary>The bundled monospace family, as its own name table spells it.</summary>
    public const string BundledMonoName = "JetBrains Mono NL";

    /// <summary>The bundled monospace family as a <see cref="FontFamily"/> source.</summary>
    public const string BundledMono = MonoCollectionKey + "#" + BundledMonoName;

    private const string Inter = "fonts:Inter#Inter";

    /// <summary>
    /// macOS's UI face, by the name CoreText answers to. <c>$Default</c> is Helvetica
    /// there: Avalonia asks Skia, and CoreText gives Helvetica for an unnamed Latin
    /// request (AvaloniaUI/Avalonia#21565). <c>.AppleSystemUIFont</c> resolves too, but
    /// to a face whose family is "System Font", which a composite family rejects as a
    /// fallback that did not match.
    /// </summary>
    private const string MacSystemFace = "System Font";

    /// <summary>
    /// The extra spacing San Francisco needs at text sizes when Avalonia draws it.
    /// <para>
    /// SF is one variable font with an optical-size axis whose default is 20, display
    /// spacing; CoreText sets the axis to the point size, so 13px text is drawn from the
    /// looser text design. Avalonia 12.1 cannot set a variation axis, and HarfBuzz reads
    /// the default instance's advances, so every glyph sits on display spacing and body
    /// text reads cramped. Chromium had the same defect on Catalina until it applied
    /// <c>opsz</c> itself (web.dev, "More variable font options for the macOS system-ui
    /// font in Chromium 83"). 0.7 is the value the Avalonia issue above found right
    /// around 13px; it is the one number here that was not measured on our own screens,
    /// so it is checked on a Mac before a release that changes it.
    /// </para>
    /// </summary>
    public const double MacSystemLetterSpacing = 0.7;

    /// <summary>
    /// The interface face an app starts with: the platform's own, everywhere. On Windows
    /// and Linux that is what windows were already drawn in (DESIGN.md rule 22 has how,
    /// by accident); on macOS it replaces Helvetica, which <c>$Default</c> gave there.
    /// </summary>
    public static InterfaceFont PlatformDefault => InterfaceFont.System;

    /// <summary>
    /// Registers the bundled monospace font. Call beside <c>WithInterFont()</c> in every
    /// app builder, tests and tools included, so <c>MonoFont</c> resolves to the same
    /// face everywhere a frame is drawn (a CI container has none of the system ones).
    /// </summary>
    public static AppBuilder WithNimbusFonts(this AppBuilder builder) =>
        builder.ConfigureFonts(manager => manager.AddFontCollection(new JetBrainsMonoFontCollection()));

    /// <summary>
    /// The interface family for <paramref name="font"/> on this platform. Inter is
    /// Fluent's own value for the key, unchanged; a system face has Inter behind it
    /// where the name might not resolve.
    /// </summary>
    public static FontFamily Interface(InterfaceFont font) => new(font switch
    {
        InterfaceFont.Inter => $"{Inter}, {FontFamily.DefaultFontFamilyName}",
        _ when OperatingSystem.IsMacOS() => $"{MacSystemFace}, {Inter}",
        _ => InterfaceSource(font),
    });

    /// <summary>
    /// The monospace family: <paramref name="familyName"/> if it is set, with the bundled
    /// face behind it for a font that has since been uninstalled; the bundled face alone
    /// otherwise.
    /// </summary>
    public static FontFamily Mono(string? familyName) =>
        new(string.IsNullOrWhiteSpace(familyName) || familyName == BundledMonoName
            ? BundledMono
            : $"{familyName.Trim()}, {BundledMono}");

    /// <summary>
    /// The family for text that spells a shortcut: the interface face first, so letters
    /// and the Ctrl scheme's words match the text beside them, then faces that carry
    /// Apple's key glyphs (⌘ ⇧ ⌥ ⌃ ↩ ⎋ ⌫ ⇥) at text size. Inter has none of them, and
    /// macOS's own fallback, Apple Symbols, draws them at half the height of a letter.
    /// </summary>
    public static FontFamily KeyCap(InterfaceFont font) => new($"{InterfaceSource(font)}, Lucida Grande, Segoe UI Symbol");

    /// <summary>
    /// The letter spacing every top level draws its interface text with: the San Francisco
    /// compensation (<see cref="MacSystemLetterSpacing"/>) when that face is in use, nothing
    /// otherwise. Text in the <c>mono</c> class resets it to zero.
    /// </summary>
    public static double InterfaceLetterSpacing(InterfaceFont font) =>
        font == InterfaceFont.System && OperatingSystem.IsMacOS() && ResolvesMacSystemFace() ? MacSystemLetterSpacing : 0;

    /// <summary>
    /// Sets the four typography resources in <paramref name="resources"/> (the
    /// application's own, which every lookup reaches before the theme's). Every use site
    /// reads them as <c>DynamicResource</c>, so open windows change in place.
    /// </summary>
    public static void Apply(IResourceDictionary resources, InterfaceFont font, string? monoFamily)
    {
        resources["ContentControlThemeFontFamily"] = Interface(font);
        resources["KeyCapFont"] = KeyCap(font);
        resources["InterfaceLetterSpacing"] = InterfaceLetterSpacing(font);
        resources["MonoFont"] = Mono(monoFamily);
    }

    /// <summary>The face's own name, with no fallback behind it.</summary>
    private static string InterfaceSource(InterfaceFont font) => font switch
    {
        InterfaceFont.Inter => Inter,
        _ when OperatingSystem.IsMacOS() => MacSystemFace,
        // Segoe UI, not Segoe UI Variable: the variable face has the same optical-size
        // problem as San Francisco and no compensation measured for it.
        _ when OperatingSystem.IsWindows() => "Segoe UI",
        _ => FontFamily.DefaultFontFamilyName,
    };

    /// <summary>
    /// Whether "System Font" resolves to itself rather than to a fallback, so the
    /// compensation is never added to Inter on a macOS where the name stops working.
    /// </summary>
    private static bool ResolvesMacSystemFace() =>
        FontManager.Current.TryGetGlyphTypeface(new Typeface(MacSystemFace), out var face)
        && face.FamilyName.Contains(MacSystemFace, StringComparison.OrdinalIgnoreCase);
}
