using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Nimbus.Ui.Chrome;

/// <summary>
/// Merges an app's command bar into the window's title bar, so the shell has one
/// bar of chrome at the top instead of two.
/// <para>
/// Both Nimbus apps carry a 40px command bar directly under the OS title bar, and
/// the OS bar above it was spending ~32px on a window title the command bar was
/// printing again just below it. That height comes out of the panel underneath —
/// kubeNimbus's ~300px inspector dock, pgNimbus's results grid — permanently, on
/// every window.
/// </para>
/// <para>
/// <b>Windows and macOS only, deliberately.</b> Both keep the caption buttons in a
/// conventional corner that can be left empty, which is what every comparable app
/// does (VS Code, Chrome, Explorer, Lens, TablePlus). Extending the client area on
/// Linux hands the app the whole frame instead — X11 requests all four drawn parts,
/// not just the title bar — and client-side decorations that match GNOME look wrong
/// on KDE and on every tiling WM. It is also gated behind
/// <c>X11PlatformOptions.EnableDrawnDecorations</c>, which Avalonia marks
/// experimental. ~36px is not worth any of that: on Linux this is a no-op and the
/// window keeps its system decorations.
/// </para>
/// </summary>
public static class NimbusWindowChrome
{
    /// <summary>
    /// Fallback width of one caption button, used only if the theme's own
    /// <c>CaptionButtonWidth</c> resource cannot be read. Avalonia exposes no
    /// measurement of the caption strip — <see cref="Window.WindowDecorationMargin"/>
    /// reports the title bar's height, not the buttons' width — so the reserve is
    /// derived from the same resource the buttons size themselves from.
    /// </summary>
    private const double FallbackCaptionButtonWidth = 45;

    /// <summary>Minimize, maximize/restore, close — the three the decorations template draws.</summary>
    private const int CaptionButtonCount = 3;

    /// <summary>macOS traffic lights, which sit top-<em>left</em>: 3 × 14 plus the standard insets.</summary>
    private const double MacTrafficLightsWidth = 78;

    /// <summary>
    /// Resource key of the <c>WindowDrawnDecorations</c> control theme in
    /// <c>Chrome/Decorations.axaml</c>. Looked up by name rather than referenced
    /// directly so an app can substitute its own without forking this file.
    /// </summary>
    public const string DecorationsThemeKey = "CommandBarWindowDecorations";

    /// <summary>
    /// Extends the client area under <paramref name="commandBar"/> and keeps the
    /// caption reserve in step with the buttons for the window's lifetime.
    /// <para>
    /// <b>On Windows the caption buttons become ours, and that is not optional.</b>
    /// Avalonia 12's Win32 backend answers an extended client area with
    /// <c>RequestedDrawnDecorations = TitleBar</c> <em>and calls</em>
    /// <c>DisableCloseButton</c> on the HWND: the system's three buttons are switched
    /// off and the app is expected to draw them. (Pre-12's <c>PreferSystemChrome</c>
    /// did the opposite, which is what every sample online still shows.) Without a
    /// decorations theme the window ships with no way to close. macOS asks for no
    /// drawn decorations at all and keeps its traffic lights.
    /// </para>
    /// <para>
    /// The gestures a title bar owes the user — drag, double-click to maximize, the
    /// right-click window menu, Win11 Snap Layouts — are not reimplemented here. They
    /// come from <c>WindowDecorationProperties.ElementRole="TitleBar"</c> on the bar
    /// in XAML, which Win32 answers as <c>HTCAPTION</c>, and from the buttons' own
    /// Minimize/Maximize/Close roles, which map to
    /// <c>HTMINBUTTON</c>/<c>HTMAXBUTTON</c>/<c>HTCLOSE</c> — the last of those is
    /// what keeps Snap Layouts, which only appear over a real maximize button.
    /// Hand-rolling the drag from <c>BeginMoveDrag</c> reproduces one of the four and
    /// quietly loses the other three.
    /// </para>
    /// </summary>
    /// <param name="window">The window whose client area is extended.</param>
    /// <param name="commandBar">
    /// The bar that becomes the title bar. It must carry an explicit
    /// <see cref="Layoutable.Height"/> (the caption region is sized from it) and
    /// <c>ElementRole="TitleBar"</c>, and — because a <see cref="Border"/> with a null
    /// background does not hit-test — a non-null <see cref="Border.Background"/>, or
    /// the drag region only responds where a child happens to cover it.
    /// </param>
    /// <param name="rootLayout">
    /// The window's outermost layout element. A maximized window with an extended
    /// client area is deliberately sized a few pixels larger than the work area on
    /// every edge (Windows does this so its own resize borders stay grabbable), and
    /// Avalonia reports how much in <see cref="Window.OffScreenMargin"/>. Not honoring
    /// it clips whatever is at the window's edge — which, now, is the title bar's own
    /// contents.
    /// </param>
    /// <param name="inset">
    /// The bar's own horizontal breathing room, kept on both edges when a caption
    /// reserve is added on top.
    /// </param>
    public static void Attach(Window window, Border commandBar, Control rootLayout, double inset = 12)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(commandBar);
        ArgumentNullException.ThrowIfNull(rootLayout);

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        window.ExtendClientAreaToDecorationsHint = true;

        // Caption region == the bar, so the buttons fill its height rather than a 30px
        // strip floating inside a 40px row (30 is the theme's DefaultTitleBarHeight).
        // Read from the bar itself so the two cannot drift apart.
        window.ExtendClientAreaTitleBarHeightHint = commandBar.Height;

        // Windows only in practice: Avalonia's macOS backend reports it needs no drawn
        // decorations and AppKit keeps the traffic lights, while Win32 disables the
        // system buttons and asks the app for a title bar. Setting it unconditionally
        // is still right — the theme is simply never built where nothing asks for it.
        if (window.TryFindResource(DecorationsThemeKey, out var resource) && resource is ControlTheme decorations)
        {
            window.WindowDecorationsTheme = decorations;
        }

        ApplyCaptionReserve(window, commandBar, inset);
        ApplyOffScreenMargin(window, rootLayout);

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.OffScreenMarginProperty)
            {
                ApplyOffScreenMargin(window, rootLayout);
            }
            else if (e.Property == Window.WindowDecorationMarginProperty)
            {
                ApplyCaptionReserve(window, commandBar, inset);
            }
        };

        // The decorations don't exist yet at construction time, so the reserve's real
        // value arrives with the first WindowDecorationMargin change. This is the
        // backstop for a platform that never raises one.
        window.Opened += (_, _) => ApplyCaptionReserve(window, commandBar, inset);
    }

    /// <summary>
    /// Leaves the caption buttons their space, and takes it back the moment they are
    /// not there. Without the reserve, whatever sits at the bar's right edge ends up
    /// under Close on Windows, and whatever sits at its left edge ends up under the
    /// traffic lights on macOS. Without the taking-back, <b>full screen</b> keeps a
    /// dead 135px (or 78px) gap in a bar that no longer has any buttons in it — and on
    /// macOS the green traffic light is the ordinary way into full screen, so that is
    /// a state people reach on purpose, not a corner case.
    /// <para>
    /// <see cref="Window.WindowDecorationMargin"/> is the honest signal for "is there
    /// a caption strip over my bar right now", and it is honest on both platforms for
    /// two different reasons: with drawn decorations (Windows) its top is the title
    /// bar height only while that part is enabled, and full screen disables every
    /// part; without them (macOS) it is the backend's own extended margin, which that
    /// backend zeroes in full screen. Zero either way — and zero on Linux, where the
    /// client area was never extended.
    /// </para>
    /// </summary>
    private static void ApplyCaptionReserve(Window window, Border commandBar, double inset)
    {
        var hasCaption = window.WindowDecorationMargin.Top > 0;

        commandBar.Padding = new Thickness(
            inset + (hasCaption && OperatingSystem.IsMacOS() ? MacTrafficLightsWidth : 0),
            0,
            inset + (hasCaption && OperatingSystem.IsWindows() ? CaptionButtonsWidth(window) : 0),
            0);
    }

    private static void ApplyOffScreenMargin(Window window, Control rootLayout) =>
        rootLayout.Margin = window.OffScreenMargin;

    /// <summary>
    /// Width of the caption strip the decorations template draws over the right of the
    /// command bar, taken from the same <c>CaptionButtonWidth</c> resource the buttons
    /// size themselves from — so restyling the buttons moves the reserve with them
    /// instead of silently sliding the bar's rightmost control under Close. In DIPs,
    /// so it survives a DPI change.
    /// </summary>
    private static double CaptionButtonsWidth(Window window)
    {
        var width = window.TryFindResource("CaptionButtonWidth", out var resource) && resource is double value
            ? value
            : FallbackCaptionButtonWidth;

        return width * CaptionButtonCount;
    }
}
