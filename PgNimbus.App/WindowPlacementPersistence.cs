using Avalonia;
using Avalonia.Controls;
using PgNimbus.Core.Settings;

namespace PgNimbus.App;

/// <summary>
/// Restores and persists the main window's placement (position, size,
/// maximized state) across sessions — the window counterpart of the
/// workspace restore. Call <see cref="Attach"/> once from
/// <c>App.BuildMainWindow</c>, after construction and before the window is
/// shown, so the first frame already paints at the restored placement
/// instead of jumping there.
/// </summary>
internal static class WindowPlacementPersistence
{
    /// <summary>
    /// How much of the title bar must land on a live screen's working area for
    /// a saved position to be trusted — enough to grab and drag the window
    /// back, so a monitor unplugged since the last run can't strand it
    /// off-screen.
    /// </summary>
    private const int MinVisibleDip = 48;

    public static void Attach(Window window, WindowPlacementStore store)
    {
        Restore(window, store.Load());

        // Track the last Normal-state bounds so a window closed maximized (or
        // minimized) still saves the geometry it would restore to on
        // unmaximize, not the maximized rect. Seeded from the just-restored
        // (or default) placement in case the window is maximized before it is
        // ever moved.
        var normalPosition = window.Position;
        var normalWidth = window.Width;
        var normalHeight = window.Height;

        window.PositionChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal)
            {
                normalPosition = e.Point;
            }
        };

        window.SizeChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal)
            {
                normalWidth = e.NewSize.Width;
                normalHeight = e.NewSize.Height;
            }
        };

        window.Closing += (_, _) =>
        {
            // A closed-while-Normal window trusts its live geometry over the
            // tracked one — the tracked values can lag by one event during
            // maximize/restore transitions. Minimized never persists as a
            // state: reopening into a hidden window would look like a hang.
            if (window.WindowState == WindowState.Normal)
            {
                normalPosition = window.Position;
                normalWidth = window.ClientSize.Width;
                normalHeight = window.ClientSize.Height;
            }

            // A window that never had explicit bounds reports NaN Width/Height
            // (never MainWindow — its XAML sets both — but this helper takes any
            // Window, and serializing NaN throws). No real geometry, nothing to
            // save.
            if (!double.IsFinite(normalWidth) || !double.IsFinite(normalHeight))
            {
                return;
            }

            var maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen;

            // Losing the placement is not worth blocking window close over.
            try
            {
                store.Save(new WindowPlacement(normalPosition.X, normalPosition.Y, normalWidth, normalHeight, maximized));
            }
            catch
            {
            }
        };
    }

    private static void Restore(Window window, WindowPlacement? placement)
    {
        if (placement is null || !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height)
            || placement.Width < 200 || placement.Height < 200)
        {
            return;
        }

        // Only trust the saved position if a grabbable strip of title bar
        // still lands on some live screen: monitors get unplugged and
        // resolutions change between runs. Position is physical pixels but
        // Width is DIPs, so the probe converts per candidate screen's own
        // scale (mixed-DPI setups scale each monitor differently).
        var screens = window.Screens.All;
        var positionUsable = screens.Any(screen =>
        {
            var probe = new PixelRect(
                placement.X,
                placement.Y,
                Math.Max((int)(placement.Width * screen.Scaling), 1),
                Math.Max((int)(MinVisibleDip * screen.Scaling), 1));
            var overlap = screen.WorkingArea.Intersect(probe);
            return overlap.Width >= (int)(MinVisibleDip * screen.Scaling) && overlap.Height > 0;
        });

        if (positionUsable)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(placement.X, placement.Y);
            window.Width = placement.Width;
            window.Height = placement.Height;
        }

        // Maximized survives even when the saved position no longer maps to a
        // live screen — the window then maximizes wherever the OS opens it,
        // and unmaximize falls back to the default centered size.
        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
