using System.Text;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Diagnostics;

/// <summary>
/// Appends critical/unhandled errors to a plain-text log file. Deliberately
/// dependency-free and UI-agnostic so it can be called from anywhere — a
/// background-thread crash handler, the query engine, or the App's global
/// exception hooks — including from a handler that runs while the process is
/// already tearing down. Every operation swallows its own failures: a logger
/// that throws while logging a crash would only mask the original error.
/// </summary>
public sealed class CrashLog
{
    // Serializes appends across threads. A crash can surface on several threads
    // at once (UI + a faulted background task), and interleaved writes would
    // corrupt the file.
    private readonly object _gate = new();

    // Keep the log from growing without bound. When it crosses this size the
    // current file is rolled to "pgnimbus.log.old" (one generation kept) before
    // the next write starts a fresh file.
    private const long MaxLogBytes = 1024 * 1024; // 1 MiB

    /// <summary>Directory that holds the log file(s). Created on demand.</summary>
    public string Directory { get; }

    /// <summary>Full path to the current log file, shown to the user in the crash dialog.</summary>
    public string FilePath { get; }

    public CrashLog(string directory)
    {
        Directory = directory;
        FilePath = Path.Combine(directory, "pgnimbus.log");
    }

    /// <summary>
    /// Records a critical error with a short describing context and the
    /// exception (message, type, and stack trace, unwinding inner exceptions).
    /// Returns the log path so a caller can surface it to the user, or
    /// <c>null</c> if even writing the log failed.
    /// </summary>
    public string? LogCritical(string context, Exception? exception) => Write(FormatEntry(context, exception));

    /// <summary>Formats a single log entry. Pure — no I/O — so it's unit-testable on its own.</summary>
    internal static string FormatEntry(string context, Exception? exception)
    {
        var entry = new StringBuilder();
        entry.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("]  ");
        entry.Append("CRITICAL  ").AppendLine(context);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            entry.Append("    ").Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                entry.AppendLine(current.StackTrace);
            }
        }

        entry.AppendLine(new string('-', 72));
        return entry.ToString();
    }

    private string? Write(string text)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                RollIfTooLarge();
                File.AppendAllText(FilePath, text);
            }

            return FilePath;
        }
        catch
        {
            // Logging a crash must never itself throw — that would replace the
            // real error with a logging error and lose both.
            return null;
        }
    }

    private void RollIfTooLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var archive = FilePath + ".old";
            File.Delete(archive); // File.Delete is a no-op when the file is absent.
            File.Move(FilePath, archive);
        }
        catch
        {
            // If rolling fails, keep appending to the existing file rather than
            // losing the entry we're about to write.
        }
    }
}

/// <summary>
/// Process-wide singleton over <see cref="CrashLog"/>, logging under the app
/// data root (<c>&lt;appdata&gt;/pgNimbus/logs/pgnimbus.log</c>). This is what
/// the App's crash hooks call; tests drive <see cref="CrashLog"/> directly
/// against a temp directory.
/// </summary>
public static class CrashLogger
{
    private static readonly CrashLog Instance =
        new(Path.Combine(AppDataPaths.GetRootDirectory(), "logs"));

    /// <summary>Directory that holds the log file(s).</summary>
    public static string LogDirectory => Instance.Directory;

    /// <summary>Full path to the current log file, shown to the user in the crash dialog.</summary>
    public static string LogFilePath => Instance.FilePath;

    /// <inheritdoc cref="CrashLog.LogCritical"/>
    public static string? LogCritical(string context, Exception? exception) =>
        Instance.LogCritical(context, exception);
}
