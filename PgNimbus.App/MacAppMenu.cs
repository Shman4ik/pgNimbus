using Avalonia.Controls;
using Avalonia.Input;

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
/// this is called. Items are found by the headers Avalonia gives them ("Hide
/// Others", and the bare "Quit"), so if a later Avalonia fixes either one, this
/// simply finds nothing to change.
/// </para>
/// </summary>
public static class MacAppMenu
{
    /// <summary>The standard Hide Others gesture, ⌥⌘H.</summary>
    public static KeyGesture HideOthersGesture { get; } = new(Key.H, KeyModifiers.Meta | KeyModifiers.Alt);

    /// <summary>
    /// Rebinds Hide Others to ⌥⌘H and names the app in the Quit item. Returns the
    /// number of items changed.
    /// </summary>
    public static int FixStandardItems(NativeMenu? appMenu, string appName)
    {
        if (appMenu is null)
        {
            return 0;
        }

        var changed = 0;
        foreach (var item in appMenu.Items.OfType<NativeMenuItem>())
        {
            if (item.Header == "Hide Others" && !HideOthersGesture.Equals(item.Gesture))
            {
                item.Gesture = HideOthersGesture;
                changed++;
            }
            else if (item.Header == "Quit")
            {
                item.Header = $"Quit {appName}";
                changed++;
            }
        }

        return changed;
    }

    /// <summary>The app menu's Settings… item (App.axaml), if the menu has one.</summary>
    public static NativeMenuItem? SettingsItem(NativeMenu? appMenu) =>
        appMenu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Settings…");

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
