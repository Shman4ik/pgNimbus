using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace PgNimbus.App;

/// <summary>
/// Keeps a window's OS chrome in step with the active theme:
/// <list type="bullet">
/// <item>Title-bar icon — black line art on the light theme, light line art
/// on the dark theme. The taskbar icon always uses the light line art (see
/// <see cref="ApplyNativeIcon"/>) since the taskbar itself is almost always
/// dark, independent of the app's theme.</item>
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
    private static readonly Lazy<byte[]> LightIcoBytes = new(() => LoadBytes("window-icon-light.ico"));
    private static readonly Lazy<byte[]> DarkIcoBytes = new(() => LoadBytes("window-icon-dark.ico"));
    private static readonly Lazy<WindowIcon> LightThemeIcon = new(() => new WindowIcon(new MemoryStream(LightIcoBytes.Value)));
    private static readonly Lazy<WindowIcon> DarkThemeIcon = new(() => new WindowIcon(new MemoryStream(DarkIcoBytes.Value)));

    // Raw HICON handed to Win32 directly for the taskbar (see ApplyNativeIcon)
    // — built once and reused across every window for the app's lifetime,
    // same as Avalonia's own internal icon caching; never explicitly destroyed.
    private static readonly Lazy<(IntPtr Small, IntPtr Big)> DarkNativeIcons = new(() => CreateNativeIcons(DarkIcoBytes.Value));

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
        ApplyNativeIcon(window);
    }

    private static byte[] LoadBytes(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://PgNimbus.App/Assets/{name}"));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Window.Icon above reliably updates the title bar but, on some Windows
    /// 11 builds, not the taskbar button — a known Avalonia/Win32 gap
    /// (AvaloniaUI/Avalonia#12343, #11569: Avalonia's WM_SETICON doesn't
    /// always land for the taskbar's HICON). Send WM_SETICON directly with
    /// icons built from the same .ico bytes so both surfaces agree.
    /// <para>
    /// Unlike the title bar (whose background <see cref="ApplyCaptionColor"/>
    /// pins to match the icon), the taskbar itself is chrome the app doesn't
    /// control — it's dark on the overwhelming majority of Windows installs
    /// regardless of the app's own theme. So the taskbar HICON always uses
    /// the dark-theme (light-ink) icon; only the title bar follows the app
    /// theme.
    /// </para>
    /// </summary>
    private static void ApplyNativeIcon(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { } handle)
        {
            return; // not opened yet; the Opened hook re-applies
        }

        // A zero HICON here means the .ico was missing that size or Win32
        // refused it — sending WM_SETICON with NULL would *remove* the icon,
        // so skip and leave whatever Window.Icon already put there.
        var icons = DarkNativeIcons.Value;
        if (icons.Small != IntPtr.Zero)
        {
            SendMessage(handle.Handle, WmSeticon, (IntPtr)IconSmall, icons.Small);
        }

        if (icons.Big != IntPtr.Zero)
        {
            SendMessage(handle.Handle, WmSeticon, (IntPtr)IconBig, icons.Big);
        }
    }

    private static (IntPtr Small, IntPtr Big) CreateNativeIcons(byte[] icoBytes) =>
        (CreateIcon(icoBytes, 16), CreateIcon(icoBytes, 32));

    private static IntPtr CreateIcon(byte[] icoBytes, int size)
    {
        return ExtractIcoEntry(icoBytes, size) is { } entry
            ? CreateIconFromResourceEx(entry, (uint)entry.Length, true, 0x00030000, size, size, 0)
            : IntPtr.Zero;
    }

    /// <summary>
    /// Pulls one image's raw bytes (always a PNG blob here) out of an
    /// in-memory .ico by exact pixel size. Returns null when the size is
    /// missing (e.g. make-app-icons.ps1 drifted from the sizes expected
    /// here): this whole path is cosmetic, so it must degrade to Avalonia's
    /// plain Window.Icon behavior rather than crash window construction.
    /// </summary>
    private static byte[]? ExtractIcoEntry(byte[] ico, int size)
    {
        var count = BitConverter.ToUInt16(ico, 4);
        for (var i = 0; i < count; i++)
        {
            var entryOffset16 = 6 + i * 16;
            var width = ico[entryOffset16] == 0 ? 256 : ico[entryOffset16];
            if (width != size)
            {
                continue;
            }

            var byteCount = BitConverter.ToUInt32(ico, entryOffset16 + 8);
            var dataOffset = BitConverter.ToUInt32(ico, entryOffset16 + 12);
            return ico[(int)dataOffset..(int)(dataOffset + byteCount)];
        }

        return null;
    }

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

    private const uint WmSeticon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(
        byte[] pbIconBits, uint cbIconBits, [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVersion, int cxDesired, int cyDesired, uint flags);
}
