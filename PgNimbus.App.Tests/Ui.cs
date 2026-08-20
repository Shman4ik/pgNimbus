using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using PgNimbus.App;
using PgNimbus.Core.Commands;

namespace PgNimbus.App.Tests;

/// <summary>
/// Runs a test body on a real Avalonia UI thread with the headless platform
/// behind it, so a test can construct the actual windows, send real key and
/// pointer input, and read what the controls ended up holding.
///
/// Why these tests exist at all: <c>PgNimbus.Core.Tests</c> covers the engine
/// and the pure logic, and the screenshot harness proves every view *renders* —
/// but nothing proved that pressing a key reaches a command, that a tab strip
/// still opens tabs, or that the palette invokes the entry it highlights. That
/// is the part of a release that was being checked by hand.
///
/// The session is Avalonia's own <see cref="HeadlessUnitTestSession"/>, started
/// once for the process and shut down by <see cref="Shutdown"/> below. Note it
/// is started via <see cref="AvaloniaTestApplicationAttribute"/> on this
/// assembly, which points at <see cref="TestApp.BuildAvaloniaApp"/> — the app is
/// set up but never given a lifetime, so
/// <see cref="App.OnFrameworkInitializationCompleted"/> (which would read the
/// developer's real settings and could auto-connect to their last database)
/// never runs.
/// </summary>
public static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> SessionHolder =
        new(() => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Ui).Assembly));

    private static HeadlessUnitTestSession Session => SessionHolder.Value;

    /// <summary>
    /// Runs <paramref name="body"/> on the UI thread and waits for it to finish.
    ///
    /// Deliberately the only overload, and deliberately routed through the
    /// <c>Func&lt;Task&lt;T&gt;&gt;</c> shape of <c>Dispatch</c>. Handing an async
    /// lambda to either of the session's other overloads compiles and runs, and
    /// is silently wrong: <c>Dispatch&lt;T&gt;(Func&lt;T&gt;)</c> with
    /// <c>T = Task</c> hands back a <c>Task&lt;Task&gt;</c> whose outer task
    /// completes the moment the body *returns* its task, so the dispatcher stops
    /// pumping early and every assertion after the first await lands on a task
    /// nobody observes — the whole suite passes without running. That is not
    /// hypothetical; it is how this file was written the first time, and the
    /// only reason it was caught was deliberately breaking an assertion to check
    /// the tests could still fail. Keep it as one async-only overload so the
    /// mistake is not expressible.
    /// </summary>
    public static Task Run(Func<Task> body) =>
        Session.Dispatch<object?>(
            async () =>
            {
                await body();
                return null;
            },
            CancellationToken.None);

    /// <summary>
    /// Shows a window and lets layout settle. Every test that sends input needs
    /// this: input routing goes through the visual tree, and an unshown window
    /// has none.
    /// </summary>
    public static void Show(Window window, ThemeVariant? theme = null)
    {
        if (theme is not null)
        {
            Application.Current!.RequestedThemeVariant = theme;
        }

        window.Show();
        Settle();
    }

    /// <summary>
    /// Drains the dispatcher queue and forces a render pass. Layout, bindings
    /// and posted continuations all land on the queue, so an assertion made
    /// straight after an input call is reading state that has not happened yet.
    /// </summary>
    public static void Settle(int passes = 2)
    {
        for (var i = 0; i < passes; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds, or gives
    /// up. For the handful of flows that hop through an <c>async</c> command:
    /// a fixed number of <see cref="Settle"/> passes would either be flaky or
    /// pessimistically slow.
    /// </summary>
    public static bool SettleUntil(Func<bool> condition, int maxPasses = 200)
    {
        for (var i = 0; i < maxPasses; i++)
        {
            if (condition())
            {
                return true;
            }

            Settle(passes: 1);
        }

        return condition();
    }

    /// <summary>
    /// Sends the gesture the command catalog declares for <paramref name="id"/>.
    ///
    /// Deliberately resolved rather than typed in: the catalog is the single
    /// declaration of every chord (CLAUDE.md, UI design rule 5), so a test that
    /// hardcoded Ctrl+K would keep passing after the chord moved, and would fail
    /// on macOS where the same entry resolves to Cmd+K.
    /// </summary>
    public static void Press(TopLevel target, CommandId id)
    {
        var gesture = CommandBindings.GestureFor(id)
            ?? throw new InvalidOperationException($"{id} has no chord in the command catalog.");

        Press(target, gesture.Key, gesture.KeyModifiers);
    }

    /// <summary>Sends a literal key press.</summary>
    public static void Press(TopLevel target, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        // PhysicalKey.None: the app matches on KeyEventArgs.Key throughout
        // (KeyBinding gestures and CommandBindings.Matches both do), and there
        // is no public Key -> PhysicalKey mapping to hand a real scan code to.
        target.KeyPress(key, ToRaw(modifiers), PhysicalKey.None, null);
        Settle();
    }

    /// <summary>Types text into whatever currently holds focus.</summary>
    public static void Type(TopLevel target, string text)
    {
        target.KeyTextInput(text);
        Settle();
    }

    private static RawInputModifiers ToRaw(KeyModifiers modifiers)
    {
        var raw = RawInputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            raw |= RawInputModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            raw |= RawInputModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            raw |= RawInputModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            raw |= RawInputModifiers.Meta;
        }

        return raw;
    }

    [After(TestSession)]
    public static void Shutdown()
    {
        if (SessionHolder.IsValueCreated)
        {
            SessionHolder.Value.Dispose();
        }
    }
}

/// <summary>
/// The app builder the headless session uses. Deliberately the same one
/// <c>tools/Screenshot</c> configures, minus Skia: these tests assert on control
/// state rather than pixels, and the headless drawing stub makes them faster.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
        .WithInterFont();
}
