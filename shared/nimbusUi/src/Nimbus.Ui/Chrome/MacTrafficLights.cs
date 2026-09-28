using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Nimbus.Ui.Chrome;

/// <summary>
/// Centres the macOS traffic lights (close, minimize, zoom) on the merged command bar.
/// <para>
/// AppKit places them for its own title bar, about 28pt tall, so on a 40px command bar
/// they sat about 5pt above the bar's centre line, with every other control in the bar
/// centred on it. Avalonia 12 offers nothing to move them:
/// <see cref="Window.ExtendClientAreaTitleBarHeightHint"/> only sizes the title-bar
/// backdrop on macOS, and the native <c>SetExtendClientArea</c> always clears the window's
/// <c>NSToolbar</c> (a toolbar is how a Cocoa app gets a taller title bar with the buttons
/// centred in it; Avalonia 11's <c>OSXThickTitleBar</c> hint, which asked for one, is
/// gone). So this does what Electron's <c>trafficLightPosition</c> and Tauri do: it moves
/// the three buttons itself, through the Objective-C runtime, and does it again whenever
/// AppKit may have laid the title bar out afresh (a resize, a state change, activation),
/// since AppKit puts them back each time.
/// </para>
/// <para>
/// Only the vertical position changes; the horizontal inset is AppKit's, which is what
/// <see cref="NimbusWindowChrome"/>'s caption reserve is measured against. In full screen
/// the title bar lives in a separate reveal window and is left alone.
/// </para>
/// </summary>
internal static class MacTrafficLights
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // NSWindowButton
    private const long CloseButton = 0;
    private const long MiniaturizeButton = 1;
    private const long ZoomButton = 2;

    /// <summary>Keeps the buttons centred on a bar of <paramref name="barHeight"/> DIPs for the window's lifetime.</summary>
    public static void Attach(Window window, double barHeight)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        void Schedule() => Dispatcher.UIThread.Post(() => Apply(window, barHeight), DispatcherPriority.Render);

        window.Opened += (_, _) => Schedule();
        window.Activated += (_, _) => Schedule();
        window.Resized += (_, _) => Schedule();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Window.WindowDecorationMarginProperty)
            {
                Schedule();
            }
        };
    }

    private static void Apply(Window window, double barHeight)
    {
        // No caption strip means full screen (or no title bar at all): nothing to centre.
        if (window.WindowState == WindowState.FullScreen || window.WindowDecorationMargin.Top <= 0)
        {
            return;
        }

        try
        {
            var nsWindow = NativeWindow(window);
            if (nsWindow == IntPtr.Zero)
            {
                return;
            }

            var sel = Selectors.Instance;
            foreach (var kind in new[] { CloseButton, MiniaturizeButton, ZoomButton })
            {
                var button = MsgSendLong(nsWindow, sel.StandardWindowButton, kind);
                if (button == IntPtr.Zero)
                {
                    continue;
                }

                var container = MsgSend(button, sel.Superview);
                if (container == IntPtr.Zero)
                {
                    continue;
                }

                var frame = Frame(button, sel);
                var containerFrame = Frame(container, sel);
                var flipped = MsgSendBool(container, sel.IsFlipped);

                // The button's superview is the title bar view, whose top is the window's
                // top edge. Centre the button on barHeight / 2 measured from that edge.
                var top = Math.Round(barHeight / 2 - frame.Height / 2);
                // Clamped to the title bar view: a button moved past its bottom edge would
                // be clipped. On a bar taller than AppKit's that is as low as it goes.
                var y = Math.Max(0, flipped ? top : containerFrame.Height - top - frame.Height);
                if (Math.Abs(y - frame.Y) < 0.5)
                {
                    continue;
                }

                MsgSendPoint(button, sel.SetFrameOrigin, new NSPoint(frame.X, y));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not an Objective-C process after all; leave AppKit's placement.
        }
    }

    private static IntPtr NativeWindow(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return handle.HandleDescriptor switch
        {
            "NSWindow" => handle.Handle,
            "NSView" => MsgSend(handle.Handle, Selectors.Instance.Window),
            _ => IntPtr.Zero,
        };
    }

    private static NSRect Frame(IntPtr view, Selectors sel) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? FrameStret(view, sel.Frame)
            : MsgSendRect(view, sel.Frame);

    // x86_64 returns a 32-byte struct through a hidden pointer (objc_msgSend_stret);
    // arm64 returns an all-double struct in d0-d3 through plain objc_msgSend.
    private static NSRect FrameStret(IntPtr view, IntPtr selector)
    {
        MsgSendStret(out var rect, view, selector);
        return rect;
    }

    private sealed class Selectors
    {
        public static readonly Selectors Instance = new();

        public readonly IntPtr StandardWindowButton = sel_registerName("standardWindowButton:");
        public readonly IntPtr Superview = sel_registerName("superview");
        public readonly IntPtr Frame = sel_registerName("frame");
        public readonly IntPtr IsFlipped = sel_registerName("isFlipped");
        public readonly IntPtr SetFrameOrigin = sel_registerName("setFrameOrigin:");
        public readonly IntPtr Window = sel_registerName("window");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NSPoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NSRect(double X, double Y, double Width, double Height);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendLong(IntPtr receiver, IntPtr selector, long arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendBool(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern NSRect MsgSendRect(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static extern void MsgSendStret(out NSRect result, IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendPoint(IntPtr receiver, IntPtr selector, NSPoint point);
}
