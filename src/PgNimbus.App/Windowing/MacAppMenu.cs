using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using PgNimbus.Core.Localization;

namespace PgNimbus.App;

/// <summary>
/// Corrections to the standard block Avalonia appends to the macOS app menu.
/// <para>
/// Avalonia.Native adds Services, Hide, Hide Others, Show All and Quit to the
/// app menu itself (<c>AvaloniaNativeMenuExporter.PopulateStandardOSXMenuItems</c>),
/// and two of them are wrong as of 12.1: Hide Others is bound to ⌥⌘Q instead of
/// ⌥⌘H, one modifier away from quitting, and Quit reads "Quit" where every Mac
/// app says "Quit &lt;name&gt;". Turning the block off
/// (<c>MacOSPlatformOptions.DisableDefaultApplicationMenuItems</c>) and building
/// our own is not an option: the Hide / Hide Others / Show All commands and the
/// Services submenu flag are internal to Avalonia.Native. The block's items are
/// ordinary <see cref="NativeMenuItem"/>s added to our own app menu, though, and
/// the exporter watches their Header and Gesture, so they are corrected in place.
/// </para>
/// <para>
/// The exporter adds them during platform setup, which runs after the
/// application's XAML is loaded and before
/// <see cref="Avalonia.Application.OnFrameworkInitializationCompleted"/>, where
/// this is called. Items are found once, by the English headers Avalonia gives
/// them ("Hide Others", and the bare "Quit"), so if a later Avalonia fixes
/// either one, this simply finds nothing to change. Their labels are then
/// written from the string table (Avalonia's are English whatever the app's
/// language), and the found items are remembered, since a header the language
/// changed can no longer be used to find them again.
/// </para>
/// </summary>
public static class MacAppMenu
{
    /// <summary>The standard Hide Others gesture, ⌥⌘H.</summary>
    public static KeyGesture HideOthersGesture { get; } = new(Key.H, KeyModifiers.Meta | KeyModifiers.Alt);

    private sealed record StandardItems(
        NativeMenuItem? Hide,
        NativeMenuItem? HideOthers,
        NativeMenuItem? ShowAll,
        NativeMenuItem? Quit,
        NativeMenuItem? Services);

    private static readonly ConditionalWeakTable<NativeMenu, StandardItems> Known = new();

    private static StandardItems FindStandardItems(NativeMenu appMenu)
    {
        var items = appMenu.Items.OfType<NativeMenuItem>().ToList();
        return new StandardItems(
            items.FirstOrDefault(i => i.Header is { } h && h.StartsWith("Hide ", StringComparison.Ordinal) && h != "Hide Others"),
            items.FirstOrDefault(i => i.Header == "Hide Others"),
            items.FirstOrDefault(i => i.Header == "Show All"),
            items.FirstOrDefault(i => i.Header == "Quit"),
            items.FirstOrDefault(i => i.Header == "Services"));
    }

    /// <summary>
    /// Rebinds Hide Others to ⌥⌘H, names the app in the Quit item, and writes the
    /// block's labels in the current language (call it again after a change of
    /// language). Returns the number of corrections made to the gesture and to
    /// Quit's name, not counting a translation of a label.
    /// </summary>
    public static int FixStandardItems(NativeMenu? appMenu, string appName)
    {
        if (appMenu is null)
        {
            return 0;
        }

        if (!Known.TryGetValue(appMenu, out var found))
        {
            found = FindStandardItems(appMenu);
            if (found.HideOthers is not null || found.Quit is not null)
            {
                Known.AddOrUpdate(appMenu, found);
            }
        }

        var changed = 0;
        Label(found.Hide, Strings.Shell_MacHide(appName));
        Label(found.ShowAll, Strings.Shell_MacShowAll);
        Label(found.Services, Strings.Shell_MacServices);
        Label(found.HideOthers, Strings.Shell_MacHideOthers);
        if (found.HideOthers is { } hideOthers && !HideOthersGesture.Equals(hideOthers.Gesture))
        {
            hideOthers.Gesture = HideOthersGesture;
            changed++;
        }

        if (found.Quit is { } quit && quit.Header != Strings.Shell_MacQuit(appName))
        {
            quit.Header = Strings.Shell_MacQuit(appName);
            changed++;
        }

        return changed;

        static void Label(NativeMenuItem? item, string header)
        {
            if (item is not null && item.Header != header)
            {
                item.Header = header;
            }
        }
    }

    /// <summary>
    /// The app menu's Settings… item (App.axaml), if the menu has one. Found by
    /// its gesture, ⌘, : its header changes with the language.
    /// </summary>
    public static NativeMenuItem? SettingsItem(NativeMenu? appMenu) =>
        appMenu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i =>
            i.Gesture is { Key: Key.OemComma } gesture && gesture.KeyModifiers == KeyModifiers.Meta);

    /// <summary>
    /// Enables Settings… only while a main window is open. The preferences page
    /// is an overlay on a connected window, so with only the connection dialog up
    /// the item used to sit there enabled and do nothing when chosen.
    /// </summary>
    public static void UpdateSettingsItem(NativeMenu? appMenu, bool hasMainWindow)
    {
        if (SettingsItem(appMenu) is { } item)
        {
            item.IsEnabled = hasMainWindow;
        }
    }
}
