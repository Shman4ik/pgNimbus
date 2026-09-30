using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PgNimbus.App;

/// <summary>
/// The OS chrome of a <b>modal</b> dialog (one shown through <c>ShowDialog</c>):
/// everything <see cref="ThemedWindowChrome"/> does, plus two rules that only make
/// sense for a window that blocks its owner until it is answered.
/// <list type="bullet">
/// <item><b>No minimize, no maximize, on every platform.</b> A minimized modal
/// leaves its owner blocked with nothing on screen to answer, and a maximized
/// confirm box is a sentence in the middle of a monitor. Resizable dialogs stay
/// resizable (<see cref="Window.CanResize"/> is untouched); on macOS turning off
/// maximize also turns off the green button's full screen, which is the point.</item>
/// <item><b>On macOS, the caption says nothing.</b> Every dialog already names
/// itself in its body (<c>TextBlock.dialogTitle</c>), and AppKit printed the same
/// words again in the title bar above it, over two greyed-out traffic lights. The
/// client area is extended under the title bar, which is how Avalonia's macOS
/// backend hides the caption text (<c>NSWindowTitleHidden</c> with a transparent
/// title bar): the close button stays, the window keeps its
/// <see cref="Window.Title"/> for VoiceOver, Mission Control and the Window menu,
/// and the body heading is the only title on screen. A transparent strip with the
/// <c>TitleBar</c> role keeps the band draggable, and the content moves down by the
/// band's height so nothing sits under the traffic lights. Windows and Linux keep
/// their OS caption.</item>
/// </list>
/// <para>
/// <b>Not a sheet.</b> A macOS modal would ideally slide out of its owner's title
/// bar (<c>beginSheet:</c>). Avalonia 12 has no sheet presentation: neither
/// <see cref="Window"/> nor its platform interface exposes one, and
/// <c>ShowDialog</c> on macOS is an owned window plus Avalonia's own modal loop.
/// Driving <c>beginSheet:</c> through the native handle would put AppKit's sheet
/// modality underneath that loop, which is not a low-risk change for a dialog
/// helper, so dialogs stay owned, centred windows.
/// </para>
/// Call <see cref="Attach"/> once from the dialog's constructor, after
/// <c>InitializeComponent</c>, in place of <see cref="ThemedWindowChrome.Attach"/>.
/// </summary>
public static class DialogChrome
{
    public static void Attach(Window window)
    {
        ThemedWindowChrome.Attach(window);

        window.CanMinimize = false;
        window.CanMaximize = false;

        if (OperatingSystem.IsMacOS())
        {
            HideMacCaption(window);
        }
    }

    private static void HideMacCaption(Window window)
    {
        if (window.Content is not Control content)
        {
            return;
        }

        window.ExtendClientAreaToDecorationsHint = true;

        // The strip sits over the content's top edge rather than above it, so the
        // band the traffic lights share stays one surface with the dialog body.
        var dragStrip = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
        };
        WindowDecorationProperties.SetElementRole(dragStrip, WindowDecorationsElementRole.TitleBar);

        var baseMargin = content.Margin;
        window.Content = null;
        var host = new Panel();
        host.Children.Add(content);
        host.Children.Add(dragStrip);
        window.Content = host;

        void Apply()
        {
            // Zero until the platform reports the title bar (and in the headless
            // tests, always), so the layout is the plain one until then.
            var band = window.WindowDecorationMargin.Top;
            dragStrip.Height = band;
            dragStrip.IsVisible = band > 0;
            content.Margin = new Thickness(baseMargin.Left, baseMargin.Top + band, baseMargin.Right, baseMargin.Bottom);
        }

        Apply();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowDecorationMarginProperty)
            {
                Apply();
            }
        };
        window.Opened += (_, _) => Apply();
    }
}
