using Avalonia;
using Nimbus.Ui.Fonts;
using PgNimbus.App.Diagnostics;
using PgNimbus.Core.Diagnostics;
using PgNimbus.App.Platform;
using PgNimbus.Core.Localization;
using PgNimbus.Core.Settings;

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
            // Before anything builds a window, so the first frame is in the right language.
            LoadLanguage();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            // The last window's Closed started its connection teardown (rollback,
            // pool, tunnel) on the thread pool; give it a bounded moment.
            ConnectionCleanup.WaitForPending(ConnectionCleanup.ExitWait);
        }
        catch (Exception ex)
        {
            // An exception escaping the message loop means the app is going
            // down. Log the crash and show the user the error window (with the
            // log path and a one-click GitHub issue) before the process exits.
            CrashReporter.HandleFatal(ex);
        }
    }

    /// <summary>
    /// The language from Settings (or the OS's), unless <c>PGNIMBUS_TRANSLATION_FILE</c>
    /// names a <c>Strings.&lt;tag&gt;.resx</c> to try: a translator sees their file in a
    /// released build without building pgNimbus (docs/dev/translating.md).
    /// </summary>
    private static void LoadLanguage()
    {
        if (Environment.GetEnvironmentVariable("PGNIMBUS_TRANSLATION_FILE") is { Length: > 0 } path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var name = Path.GetFileNameWithoutExtension(path);
                var tag = name.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase) ? name["Strings.".Length..] : name;
                Localizer.LoadTable(tag, Localizer.Read(stream));
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                CrashLogger.LogCritical("PGNIMBUS_TRANSLATION_FILE could not be read", e);
            }
        }

        Localizer.Load(Localizer.Resolve(new AppSettingsStore().Load().Language, SystemLanguages.Preferred()));
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .WithNimbusFonts()
        .LogToTrace();
}
