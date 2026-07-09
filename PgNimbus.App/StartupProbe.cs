using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace PgNimbus.App;

/// <summary>
/// Headless startup measurement, used by <c>scripts/benchmarks/run-benchmarks.sh</c>
/// and the Benchmarks CI workflow. When <c>PGNIMBUS_STARTUP_PROBE=1</c>, the app
/// prints one machine-readable line after its first window has rendered its first
/// frame and then exits:
///
/// <code>PGNIMBUS_STARTUP_PROBE window_ms=123 rss_bytes=45678901</code>
///
/// <c>window_ms</c> is measured from OS process start (not <c>Main</c>), so it
/// includes process spawn and runtime init — the number a user actually
/// experiences between double-click and a visible window.
/// </summary>
internal static class StartupProbe
{
    public static void ArmIfRequested(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Environment.GetEnvironmentVariable("PGNIMBUS_STARTUP_PROBE") != "1" ||
            desktop.MainWindow is not { } window)
        {
            return;
        }

        window.Opened += (_, _) =>
            // Opened fires before the first render pass. A Background-priority
            // dispatcher callback queues behind Render, so by the time it runs
            // the first frame has actually been drawn.
            Dispatcher.UIThread.Post(() =>
            {
                using var process = Process.GetCurrentProcess();
                var elapsed = DateTime.Now - process.StartTime;
                Console.WriteLine(
                    $"PGNIMBUS_STARTUP_PROBE window_ms={elapsed.TotalMilliseconds:F0} rss_bytes={process.WorkingSet64}");
                desktop.Shutdown();
            }, DispatcherPriority.Background);
    }
}
