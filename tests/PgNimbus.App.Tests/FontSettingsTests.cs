using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Nimbus.Ui.Fonts;
using PgNimbus.App.Platform;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Screenshot;
using SkiaSharp;

namespace PgNimbus.App.Tests;

/// <summary>
/// The interface face, the code face and the editor size are settings (DESIGN.md rule
/// 22). Each is one resource every use site reads dynamically, so these tests change
/// the resource under an open window and read what the controls picked up. The
/// headless session draws with a stub, so which face a family resolves to is checked
/// against the font files themselves, through Skia.
/// </summary>
public class FontSettingsTests
{
    [Test]
    public async Task The_bundled_face_is_the_one_its_name_says_and_is_monospace_without_ligatures()
    {
        // In the session: assets resolve through the platform's loader.
        await Ui.Run(async () =>
        {
            using var regular = Typeface("avares://Nimbus.Ui/Fonts/JetBrainsMono/JetBrainsMonoNL-Regular.ttf");
            using var bold = Typeface("avares://Nimbus.Ui/Fonts/JetBrainsMono/JetBrainsMonoNL-Bold.ttf");

            // The family string every mono text resolves through names the font's own
            // family; a mismatch would fall back to the platform default, proportional.
            await Assert.That(regular.FamilyName).IsEqualTo(NimbusFonts.BundledMonoName);
            await Assert.That(bold.FamilyName).IsEqualTo(NimbusFonts.BundledMonoName);
            await Assert.That(bold.FontWeight).IsGreaterThan(regular.FontWeight);
            await Assert.That(MonospaceFonts.IsMonospace(regular)).IsTrue();

            // NL: no contextual alternates, which is where the regular cut keeps "->>" as
            // an arrow and "!=" as ≠.
            await Assert.That(regular.GetTableTags().Contains(Tag("GSUB")) && HasCalt(regular)).IsFalse();
        });
    }

    [Test]
    public async Task A_proportional_face_is_not_offered_as_a_code_font()
    {
        await Ui.Run(async () =>
        {
            using var inter = Typeface("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf");
            await Assert.That(MonospaceFonts.IsMonospace(inter)).IsFalse();
        });
    }

    [Test]
    public async Task A_chosen_face_reaches_open_text_and_the_mono_class_keeps_its_own_face_and_spacing()
    {
        await Ui.Run(async () =>
        {
            var plain = new TextBlock { Text = "Interface" };
            var mono = new TextBlock { Text = "SELECT 1", Classes = { "mono" } };
            var window = new Window { Content = new StackPanel { Children = { plain, mono } }, Width = 300, Height = 100 };
            Ui.Show(window);

            // The window's own resources stand in for the application's, which every
            // other test shares: lookup reaches them first all the same.
            NimbusFonts.Apply(window.Resources, InterfaceFont.Inter, "Some Mono");
            window.Resources["InterfaceLetterSpacing"] = 0.7;
            Ui.Settle();

            await Assert.That(Names(plain.FontFamily)).IsEqualTo(Names(NimbusFonts.Interface(InterfaceFont.Inter)));
            await Assert.That(plain.LetterSpacing).IsEqualTo(0.7);
            await Assert.That(Names(mono.FontFamily)).IsEqualTo($"Some Mono, {NimbusFonts.BundledMonoName}");
            await Assert.That(mono.LetterSpacing).IsEqualTo(0d);

            // Changed again with the window open: no reopen needed.
            NimbusFonts.Apply(window.Resources, InterfaceFont.System, null);
            Ui.Settle();
            await Assert.That(Names(plain.FontFamily)).IsEqualTo(Names(NimbusFonts.Interface(InterfaceFont.System)));
            await Assert.That(Names(mono.FontFamily)).IsEqualTo(NimbusFonts.BundledMonoName);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task Inter_is_Fluents_own_value_and_its_key_cap_face_is_unchanged()
    {
        // Inter is Fluent's own value for the key, which popups were drawn in before
        // this was a setting, and the key-cap face as it was.
        await Assert.That(Names(NimbusFonts.Interface(InterfaceFont.Inter))).IsEqualTo("Inter, $Default");
        await Assert.That(Names(NimbusFonts.KeyCap(InterfaceFont.Inter))).IsEqualTo("Inter, Lucida Grande, Segoe UI Symbol");
        await Assert.That(NimbusFonts.InterfaceLetterSpacing(InterfaceFont.Inter)).IsEqualTo(0d);
        await Assert.That(App.InterfaceFontFromString("auto")).IsEqualTo(NimbusFonts.PlatformDefault);
        await Assert.That(App.InterfaceFontFromString("inter")).IsEqualTo(InterfaceFont.Inter);
        await Assert.That(App.InterfaceFontFromString("system")).IsEqualTo(InterfaceFont.System);
    }

    [Test]
    public async Task The_editor_follows_the_size_setting_after_a_zoom_and_resets_to_it()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Results();
            Ui.Show(window);
            var editor = window.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "SqlEditor");

            window.Resources[QueryEditorPanel.EditorFontSizeKey] = 18d;
            Ui.Settle();
            await Assert.That(editor.FontSize).IsEqualTo(18d);

            editor.TextArea.Focus();
            Ui.Settle();
            Ui.Press(window, Key.OemPlus, Hotkeys.Command);
            await Assert.That(editor.FontSize).IsEqualTo(19d);

            // The zoom left the resource binding in place: a size chosen in Settings
            // still reaches an editor that was zoomed.
            window.Resources[QueryEditorPanel.EditorFontSizeKey] = 16d;
            Ui.Settle();
            await Assert.That(editor.FontSize).IsEqualTo(16d);

            Ui.Press(window, Key.OemPlus, Hotkeys.Command);
            Ui.Press(window, Key.D0, Hotkeys.Command);
            await Assert.That(editor.FontSize).IsEqualTo(16d);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task Reading_the_installed_fonts_keeps_the_saved_choice_and_saves_nothing()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.Results();
            var main = (MainViewModel)window.DataContext!;
            var before = Fonts(new Core.Settings.AppSettingsStore().Load());
            var page = new PreferencesViewModel(main);

            await Assert.That(page.CodeFonts[0]).IsEqualTo(CodeFontOption.Bundled);
            await Assert.That(page.SelectedCodeFont).IsEqualTo(CodeFontOption.Bundled);

            await page.LoadInstalledCodeFontsAsync();

            await Assert.That(page.CodeFonts[0]).IsEqualTo(CodeFontOption.Bundled);
            await Assert.That(page.SelectedCodeFont).IsEqualTo(CodeFontOption.Bundled);
            await Assert.That(page.CodeFonts.Count(option => option.Name is null)).IsEqualTo(1);
            await Assert.That(Fonts(new Core.Settings.AppSettingsStore().Load())).IsEqualTo(before);

            page.Detach();
        });
    }

    private static (string, string?, double) Fonts(Core.Settings.AppSettings settings) =>
        (settings.InterfaceFont, settings.CodeFont, settings.EditorFontSize);

    private static SKTypeface Typeface(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"{uri} is not a font.");
    }

    private static uint Tag(string tag) => (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);

    /// <summary>Whether the GSUB table lists a <c>calt</c> feature (its FeatureList, by tag).</summary>
    private static bool HasCalt(SKTypeface typeface)
    {
        var gsub = typeface.GetTableData(Tag("GSUB"));
        int U16(int at) => gsub[at] << 8 | gsub[at + 1];
        var featureList = U16(6);
        var count = U16(featureList);
        for (var i = 0; i < count; i++)
        {
            var record = featureList + 2 + i * 6;
            if (gsub[record] == 'c' && gsub[record + 1] == 'a' && gsub[record + 2] == 'l' && gsub[record + 3] == 't')
            {
                return true;
            }
        }

        return false;
    }

    private static string Names(Avalonia.Media.FontFamily family) => string.Join(", ", family.FamilyNames);
}
