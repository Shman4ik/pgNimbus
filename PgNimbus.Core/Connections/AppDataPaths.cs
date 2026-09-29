namespace PgNimbus.Core.Connections;

/// <summary>
/// Where pgNimbus keeps its local application data. Every path can be null:
/// that is the "no directory resolves" state (see <see cref="ResolveDefaultRoot"/>),
/// in which every store runs from memory for the session — <see cref="Settings.AppDataFile"/>
/// reads a null path as "nothing saved" and drops writes to one.
/// </summary>
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
    /// Test seam: stands in for the environment when set, so a test can make
    /// the root unavailable (return null) without blanking the process's
    /// <c>HOME</c> under the tests running beside it. Mark such a test
    /// <c>[NotInParallel]</c>: the seam is process-wide.
    /// </summary>
    internal static Func<string?>? RootResolverForTests { get; set; }

    /// <summary>
    /// Root directory for pgNimbus's local application data (saved connection
    /// profiles, cached credentials, etc): <see cref="OverrideVariable"/> when
    /// it is set, else <see cref="GetDefaultRootDirectory"/>. Null when no
    /// directory resolves at all.
    /// </summary>
    public static string? GetRootDirectory()
    {
        if (RootResolverForTests is { } resolver)
        {
            return resolver();
        }

        return Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } overridden
            ? overridden
            : GetDefaultRootDirectory();
    }

    /// <summary>
    /// Where the app keeps its data when nothing redirects it — the user's real
    /// data. Tests compare against this to prove they stay out of it.
    /// </summary>
    public static string? GetDefaultRootDirectory() =>
        ResolveDefaultRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("HOME"));

    /// <summary>
    /// <paramref name="fileName"/> under <see cref="GetRootDirectory"/>, or null
    /// when there is no root. What every store passes to <see cref="Settings.AppDataFile"/>
    /// when it was given no path of its own.
    /// </summary>
    public static string? Resolve(string fileName) =>
        GetRootDirectory() is { } root ? Path.Combine(root, fileName) : null;

    /// <summary>
    /// The pure half of <see cref="GetDefaultRootDirectory"/>.
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> can resolve to an
    /// empty string in minimal/containerized Linux environments (e.g. no usable
    /// passwd entry for the current UID), which would otherwise make callers
    /// silently use a path relative to the working directory; then
    /// <c>$HOME/.config</c> stands in. When neither resolves the answer is
    /// <b>null</b>, not the OS temp directory (which it was until 2026-09): on
    /// Linux that is the shared <c>/tmp</c>, where another user can pre-create
    /// <c>pgNimbus/</c> and read, or plant, everything the app writes there.
    /// A null root keeps the session in memory instead.
    /// </summary>
    public static string? ResolveDefaultRoot(string? applicationData, string? home)
    {
        if (!string.IsNullOrEmpty(applicationData))
        {
            return Path.Combine(applicationData, "pgNimbus");
        }

        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config", "pgNimbus");
    }
}
