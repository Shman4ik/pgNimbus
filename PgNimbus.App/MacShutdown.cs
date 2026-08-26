using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using PgNimbus.Core.Diagnostics;

namespace PgNimbus.App;

/// <summary>
/// macOS only: end the process ourselves the moment Avalonia's shutdown has
/// run, rather than letting AppKit finish the quit.
///
/// Cmd+Q (and the app menu's Quit, and a logout) reaches
/// <c>-[NSApplication terminate:]</c>, which asks Avalonia's app delegate
/// first. That runs the entire managed shutdown — every window closes, so the
/// workspace snapshot and the window placement are written — and answers
/// <c>NSTerminateNow</c>. AppKit then calls C's <c>exit()</c>, and that is
/// where a shipped 0.7.5 died with SIGABRT, on a quit that had already done
/// everything it needed to:
///
/// <code>
/// exit -> __cxa_finalize_ranges -> ComPtr&lt;IAvnDispatcher&gt;::~ComPtr()
///      -> MicroComVtblBase__Release          (back into managed code)
///      -> Thread::ReversePInvokeAttachOrTrapThread
///      -> ThreadStore::AttachCurrentThread -> RhFailFast -> abort()
/// </code>
///
/// <c>exit()</c> runs libAvaloniaNative's C++ static destructors, and one of
/// them releases a <c>ComPtr</c> whose vtable is a managed MicroCom proxy — a
/// reverse P/Invoke. By then NativeAOT has torn down the main thread's runtime
/// state, and calling managed code on a detached thread is a deliberate
/// fail-fast ("Attempt to execute managed code after the .NET runtime thread
/// state has been destroyed"), not an exception any handler can catch. Every
/// frame in that trace is Avalonia's or the runtime's
/// (AvaloniaUI/Avalonia#12459), so the only move available here is to never
/// reach <c>__cxa_finalize</c>: <c>_exit(2)</c> ends the process without
/// running one atexit handler or static destructor.
///
/// It hangs off the lifetime's <c>Exit</c> event, which Avalonia raises after
/// all windows have closed and only when the shutdown is really going through
/// (a window that cancels its close returns before it) — so everything the app
/// saves on close is already on disk. Two consequences worth knowing: an
/// <c>Exit</c> handler registered after this one never runs, and neither does
/// anything <c>Program.Main</c> would have done on the way out.
///
/// The programmatic <c>Shutdown()</c> callers — the crash reporter and the
/// startup probe — raise the same event and are covered by the same handler.
/// They reach process exit from the other side (the message loop ends and
/// <c>Main</c> returns), but into the identical teardown.
/// </summary>
internal static class MacShutdown
{
    /// <summary>
    /// Hooks the lifetime so a completed shutdown ends the process directly.
    /// No-op off macOS: Windows and Linux exit through this teardown cleanly,
    /// and skipping it there would buy nothing.
    /// </summary>
    public static void ExitProcessOnShutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        desktop.Exit += (_, e) => ExitNow(e.ApplicationExitCode);
    }

    private static void ExitNow(int exitCode)
    {
        try
        {
            // _exit runs no atexit handler, so nothing downstream will flush
            // these. The startup probe writes exactly one line to stdout and
            // the release pipeline's smoke check greps for it.
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch
        {
            // A console that can't be flushed is not a reason to stay alive.
        }

        try
        {
            Exit(exitCode);
        }
        catch (Exception ex)
        {
            // _exit didn't resolve. Log it and return, which leaves the old
            // behaviour (AppKit exits and the process aborts on the way out) —
            // still better than throwing out of a shutdown handler, which would
            // unwind into Objective-C.
            CrashLogger.LogCritical("Could not end the process directly on shutdown", ex);
        }
    }

    /// <summary>
    /// <c>_exit(2)</c>: ends the process immediately, skipping atexit handlers,
    /// C++ static destructors and stdio flushing. Deliberately not
    /// <c>Environment.Exit</c>, which runs the same <c>exit()</c> teardown this
    /// exists to avoid.
    /// </summary>
    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void Exit(int status);
}
