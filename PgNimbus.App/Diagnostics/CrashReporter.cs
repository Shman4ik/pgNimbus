using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PgNimbus.App.Views;
using PgNimbus.Core.Diagnostics;

namespace PgNimbus.App.Diagnostics;

/// <summary>
/// The app's global crash handling. Two concerns:
/// <list type="number">
/// <item><b>Logging</b> — every critical/unhandled error is appended to a log
/// file (<see cref="CrashLogger"/>), whatever thread it surfaces on.</item>
/// <item><b>Surfacing</b> — a fatal unhandled exception is shown to the user in
/// a <see cref="CrashWindow"/> that points at the log and offers a pre-filled
/// GitHub issue, instead of the app just vanishing.</item>
/// </list>
/// There are two paths a fatal exception can take, and both are covered:
/// one thrown while the Avalonia message loop is running is caught by the
/// dispatcher's <see cref="Dispatcher.UnhandledException"/> hook (see
/// <see cref="AttachToDispatcher"/>); one thrown during startup/shutdown —
/// before or around the loop — escapes to <c>Program.Main</c>'s catch and
/// arrives at <see cref="HandleFatal"/>.
/// </summary>
public static class CrashReporter
{
    private static int _installed;
    private static int _handling;

    /// <summary>
    /// Installs the process-wide handlers for exceptions that surface off the
    /// UI thread — background-thread crashes and unobserved faulted tasks.
    /// These can only be logged (the process is usually already terminating).
    /// Called from <c>Program.Main</c> before the app starts. Idempotent.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLogger.LogCritical(
                $"Unhandled exception on a background thread (terminating={e.IsTerminating})",
                e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.LogCritical("Unobserved task exception", e.Exception);
            // Mark observed so a background logging/telemetry fault doesn't, by
            // itself, tear the process down on top of whatever already went wrong.
            e.SetObserved();
        };
    }

    /// <summary>
    /// Hooks the UI-thread dispatcher so an exception thrown while the message
    /// loop is running (an event handler, an async-void continuation, a posted
    /// job) is logged and shown in the crash window rather than killing the
    /// process. Must be called once the dispatcher exists — from
    /// <see cref="App.OnFrameworkInitializationCompleted"/>. Note: Avalonia
    /// swallows exceptions thrown directly in a <c>DispatcherTimer.Tick</c>
    /// before they reach this hook, so don't rely on a timer to test it.
    /// </summary>
    public static void AttachToDispatcher()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // Mark handled so the dispatcher doesn't also tear the process down:
            // we're taking over — show the window, then shut the app down cleanly
            // once the user has seen it.
            e.Handled = true;
            ReportAndShutdown(e.Exception);
        };
    }

    /// <summary>
    /// Handles an exception that escaped the message loop entirely (caught in
    /// <c>Program.Main</c>): logs it and shows the crash window on the
    /// already-initialized platform via a nested dispatcher frame, since the
    /// primary loop is no longer running to render it.
    /// </summary>
    public static void HandleFatal(Exception exception)
    {
        var logPath = CrashLogger.LogCritical("Fatal unhandled exception (app is shutting down)", exception)
                      ?? CrashLogger.LogFilePath;

        if (Interlocked.Exchange(ref _handling, 1) == 1)
        {
            return; // already reporting a crash — don't stack a second window
        }

        try
        {
            ShowCrashWindowNested(exception, logPath);
        }
        catch (Exception dialogException)
        {
            LogToConsole(exception, logPath, dialogException);
        }
    }

    /// <summary>
    /// The in-loop path: log the crash, show the window on the running
    /// dispatcher, and shut the app down when it's dismissed. The app is in an
    /// unknown state after an unhandled exception, so we don't try to keep it
    /// running — the window's job is to explain and point at the log.
    /// </summary>
    private static void ReportAndShutdown(Exception exception)
    {
        var logPath = CrashLogger.LogCritical("Fatal unhandled exception (UI thread)", exception)
                      ?? CrashLogger.LogFilePath;

        if (Interlocked.Exchange(ref _handling, 1) == 1)
        {
            return; // a crash window is already up
        }

        try
        {
            var window = new CrashWindow(exception, logPath);
            window.Closed += (_, _) => Shutdown();
            window.Show();
        }
        catch (Exception dialogException)
        {
            LogToConsole(exception, logPath, dialogException);
            Shutdown();
        }
    }

    /// <summary>
    /// Shows the crash dialog on the already-initialized platform and blocks
    /// until dismissed, by pumping a nested dispatcher frame. Used from the
    /// <c>Program.Main</c> path, where the primary loop has already ended.
    /// A second <see cref="AppBuilder"/> is not an option — Avalonia allows
    /// exactly one setup per process.
    /// </summary>
    private static void ShowCrashWindowNested(Exception exception, string logPath)
    {
        if (Application.Current is null)
        {
            // The exception escaped before Avalonia finished initializing, so
            // there is no UI thread to draw on. Fall back to the console.
            throw new InvalidOperationException("Avalonia platform is not initialized; cannot show the crash window.");
        }

        var window = new CrashWindow(exception, logPath);
        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        window.Show();
        Dispatcher.UIThread.PushFrame(frame);
    }

    private static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static void LogToConsole(Exception original, string logPath, Exception dialogException)
    {
        CrashLogger.LogCritical("Failed to show the crash window", dialogException);
        Console.Error.WriteLine($"pgNimbus crashed: {original}");
        Console.Error.WriteLine($"A log was written to: {logPath}");
    }
}
