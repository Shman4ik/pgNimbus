using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PgNimbus.App.Views;

/// <summary>
/// The last-resort error window shown after an unhandled exception has already
/// crashed the app (see <see cref="PgNimbus.App.Diagnostics.CrashReporter"/>).
/// It reports what happened, points at the log file on disk, and opens a
/// pre-filled GitHub issue so a report is one click away. It carries no view
/// model and touches no app services on purpose — it must render even when the
/// rest of the app is in a broken state.
/// </summary>
public partial class CrashWindow : Window
{
    private const string RepositoryUrl = "https://github.com/Shman4ik/pgNimbus";

    private readonly string _logPath;
    private readonly string _errorSummary;

    // Parameterless ctor for the XAML designer / Avalonia's loader only.
    public CrashWindow() : this(null, CrashLoggerLogPathFallback()) { }

    public CrashWindow(Exception? exception, string logPath)
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        _logPath = logPath;
        _errorSummary = DescribeException(exception);

        ErrorText.Text = _errorSummary;
        LogPathText.Text = _logPath;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private static string CrashLoggerLogPathFallback() =>
        PgNimbus.Core.Diagnostics.CrashLogger.LogFilePath;

    /// <summary>A short, human-readable one/two-line summary of the failure for the window.</summary>
    private static string DescribeException(Exception? exception)
    {
        if (exception is null)
        {
            return "An unknown error occurred.";
        }

        var summary = new StringBuilder();
        summary.Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        if (exception.InnerException is { } inner)
        {
            summary.Append('\n').Append("→ ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
        }

        return summary.ToString();
    }

    private void OnOpenLogClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Open the containing folder rather than the file itself: there's no
            // guaranteed default handler for a ".log" extension, but every
            // platform can open a directory.
            var directory = Path.GetDirectoryName(_logPath);
            OpenExternal(string.IsNullOrEmpty(directory) ? _logPath : directory);
        }
        catch
        {
            // The path is shown in the window regardless; failing to launch a
            // file browser is not worth another error dialog.
        }
    }

    private void OnReportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            OpenExternal(BuildIssueUrl());
        }
        catch
        {
            // No browser to hand off to — the repo URL is still discoverable
            // from the About box, so swallow rather than cascade.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Builds a "new issue" URL for the repo with the title and body pre-filled
    /// from the crash summary, so reporting is a single click. GitHub reads the
    /// <c>title</c>/<c>body</c>/<c>labels</c> query parameters on the new-issue
    /// form.
    /// </summary>
    private string BuildIssueUrl()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "unknown";

        var title = $"Crash: {_errorSummary.Split('\n')[0]}";

        // Keep the URL well under the ~2000-char limit browsers/the Windows shell
        // impose on Process.Start, or a long message would make the button a
        // silent no-op. The full detail is in the attached log anyway.
        var errorDetails = _errorSummary.Length > 1000
            ? _errorSummary[..1000] + "\n… (truncated — see the attached log)"
            : _errorSummary;

        var body =
            "**What happened**\n\n" +
            "pgNimbus crashed with an unhandled error.\n\n" +
            "**Error**\n\n```\n" + errorDetails + "\n```\n\n" +
            $"**Version:** {version}\n" +
            $"**OS:** {Environment.OSVersion}\n\n" +
            "**Steps to reproduce**\n\n" +
            "1. \n2. \n\n" +
            $"_Please attach the log file: `{_logPath}`_\n";

        return $"{RepositoryUrl}/issues/new" +
               $"?labels=crash" +
               $"&title={Uri.EscapeDataString(title)}" +
               $"&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>Hands a file path or URL to the OS default handler, cross-platform.</summary>
    private static void OpenExternal(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", target);
        }
        else
        {
            Process.Start("xdg-open", target);
        }
    }
}
