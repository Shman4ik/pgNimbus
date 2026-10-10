using PgNimbus.Core.Connections;

namespace PgNimbus.Screenshot;

/// <summary>
/// Keeps a screenshot or test process out of the developer's real app data.
///
/// Every store in the app falls back to <see cref="AppDataPaths.GetRootDirectory"/>
/// when it is given no path, and several are reached from places a fixture
/// cannot inject into: <c>App</c>'s static settings and completion-usage stores
/// (written by the Preferences page and the theme toggle), the workspace, the
/// crash log. So instead of chasing each one, <see cref="Enable"/> points the
/// whole root at a throwaway directory for this process, through
/// <see cref="AppDataPaths.OverrideVariable"/>. Found the hard way (2026-09-28):
/// the save-query UI tests had been overwriting the owner's real
/// <c>saved-queries.json</c> with the fixture list.
///
/// It has to run before anything constructs a store — the stores resolve their
/// path once, in their constructors, and <c>App</c>'s are static — which is why
/// the test assembly calls it from a module initializer and the harness from
/// the first line of <c>Program</c>.
/// </summary>
public static class IsolatedAppData
{
    private static readonly Lazy<string> Root = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's stand-in for the app data root. Enables the redirect on first use.</summary>
    public static string Directory => Root.Value;

    /// <summary>
    /// Redirects the app data root for this process, and pins what the machine
    /// has installed: the search for pg_dump runs programs and finds whatever the
    /// machine rendering a frame happens to have, so every search answers the
    /// fixture's install instead (<see cref="Fixtures.PgTools"/>). Idempotent.
    /// </summary>
    public static void Enable()
    {
        _ = Root.Value;
        PgNimbus.App.Platform.PgToolCatalog.UseFixedScan(Fixtures.PgTools());
    }

    /// <summary>
    /// A fresh, not-yet-created directory under <see cref="Directory"/>, for a
    /// fixture that must start from nothing (the stores create it on first save).
    /// </summary>
    public static string NewDirectory(string prefix) =>
        Path.Combine(Directory, $"{prefix}-{Guid.NewGuid():N}");

    private static string Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "pgNimbus-isolated", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(root);

        // Always overwritten, never honoured: a developer who has pointed the
        // variable at a directory they actually use must not have tests write there.
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a run over.
            }
        };

        return root;
    }
}
