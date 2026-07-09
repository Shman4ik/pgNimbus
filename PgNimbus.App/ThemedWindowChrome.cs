using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace PgNimbus.App;

/// <summary>
/// Keeps a window's OS chrome in step with the active theme:
/// <list type="bullet">
/// <item>Title-bar/taskbar icon — black line art on the light theme, light
/// line art on the dark theme.</item>
/// <item>Windows caption color — pinned to the shell's top-bar tone so the
/// title bar and the command bar read as one surface. Without this, Windows
/// paints the caption from the OS accent/dark-mode settings, which can turn
/// it black while the app is in the light theme (making the black line-art
/// icon invisible) and flip color with focus.</item>
/// </list>
/// Call <see cref="Attach"/> once from the window's constructor.
/// </summary>
public static class ThemedWindowChrome
{
    private static readonly Lazy<WindowIcon> LightThemeIcon = new(() => Load("icon-256-light.png"));
    private static readonly Lazy<WindowIcon> DarkThemeIcon = new(() => Load("icon-256-dark.png"));

    public static void Attach(Window window)
    {
        Apply(window);
        // The Win32 handle doesn't exist until the window opens, and
        // ActualThemeVariant isn't final at construction time; this also
        // covers the in-app theme toggle and OS theme flips.
        window.Opened += (_, _) => Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var dark = window.ActualThemeVariant == ThemeVariant.Dark;
        window.Icon = dark ? DarkThemeIcon.Value : LightThemeIcon.Value;
        ApplyCaptionColor(window, dark);
    }

    private static WindowIcon Load(string name) =>
        new(AssetLoader.Open(new Uri($"avares://PgNimbus.App/Assets/{name}")));

    private const int DwmwaCaptionColor = 35; // DWMWA_CAPTION_COLOR, Windows 11+
    private const int DwmwaTextColor = 36;    // DWMWA_TEXT_COLOR,    Windows 11+

    private static void ApplyCaptionColor(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { } handle)
        {
            return; // not opened yet; the Opened hook re-applies
        }

        // The same brush the shell base is painted with, resolved for the
        // actual theme, so caption and command bar are seamless.
        var caption = window.TryFindResource(
                "SystemControlBackgroundChromeMediumLowBrush", window.ActualThemeVariant, out var res)
            && res is ISolidColorBrush brush
            ? brush.Color
            : (dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        var text = dark ? Color.FromRgb(0xF0, 0xF0, 0xF0) : Color.FromRgb(0x1B, 0x1B, 0x1B);

        var captionRef = ToColorRef(caption);
        var textRef = ToColorRef(text);
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaCaptionColor, ref captionRef, sizeof(uint));
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaTextColor, ref textRef, sizeof(uint));
    }

    private static uint ToColorRef(Color c) => (uint)(c.B << 16 | c.G << 8 | c.R);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
