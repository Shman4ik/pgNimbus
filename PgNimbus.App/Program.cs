using Avalonia;
using PgNimbus.App.Diagnostics;

namespace PgNimbus.App;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by visual designer.
    [STAThread]
    public static void Main(string[] args)
    {
        // Log background-thread / unobserved-task faults from the very start.
        CrashReporter.Install();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // An exception escaping the message loop means the app is going
            // down. Log the crash and show the user the error window (with the
            // log path and a one-click GitHub issue) before the process exits.
            CrashReporter.HandleFatal(ex);
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        .WithInterFont()
        .LogToTrace();
}
