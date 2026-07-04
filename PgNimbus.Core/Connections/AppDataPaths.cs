namespace PgNimbus.Core.Connections;

internal static class AppDataPaths
{
    /// <summary>
    /// Root directory for pgNimbus's local application data (saved connection
    /// profiles, cached credentials, etc).
    /// </summary>
    public static string GetRootDirectory() => Path.Combine(ResolveAppDataDirectory(), "pgNimbus");

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
