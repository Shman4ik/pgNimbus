namespace PgNimbus.Core.Connections;

/// <summary>Where pgNimbus keeps its local application data.</summary>
public static class AppDataPaths
{
    /// <summary>
    /// Redirects every store that falls back to <see cref="GetRootDirectory"/>
    /// (settings, workspace, saved queries, history, completion usage,
    /// connection profiles, the crash log, …) to another directory. The
    /// headless UI tests and the screenshot harness set it for their own
    /// process so nothing they do can reach the developer's real app data.
    /// </summary>
    public const string OverrideVariable = "PGNIMBUS_DATA_DIR";

    /// <summary>
    /// Root directory for pgNimbus's local application data (saved connection
    /// profiles, cached credentials, etc): <see cref="OverrideVariable"/> when
    /// it is set, else <see cref="GetDefaultRootDirectory"/>.
    /// </summary>
    public static string GetRootDirectory() =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } overridden
            ? overridden
            : GetDefaultRootDirectory();

    /// <summary>
    /// Where the app keeps its data when nothing redirects it — the user's real
    /// data. Tests compare against this to prove they stay out of it.
    /// </summary>
    public static string GetDefaultRootDirectory() => Path.Combine(ResolveAppDataDirectory(), "pgNimbus");

    /// <summary>
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> can resolve to an
    /// empty string in minimal/containerized Linux environments (e.g. no usable
    /// passwd entry for the current UID), which would otherwise make callers
    /// silently use a path relative to the working directory. Fall back to
    /// $HOME, then the OS temp directory, rather than risk that.
    /// </summary>
    private static string ResolveAppDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            return appData;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : Path.Combine(home, ".config");
    }
}
