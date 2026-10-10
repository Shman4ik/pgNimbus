using PgNimbus.Core.Backup;

namespace PgNimbus.App.Platform;

/// <summary>
/// The app's one answer to "where are pg_dump and pg_restore?". A search runs
/// a program per directory it finds, so it happens once per session, the first
/// time something asks (a backup window, the Settings card), and again only on
/// Look Again or a change of folder. Every window reads the same answer, and
/// <see cref="Changed"/> tells the open ones when it moves. UI thread only.
/// </summary>
public static class PgToolCatalog
{
    private static Task<PgToolScan>? _scan;
    private static PgToolScan? _fixed;

    /// <summary>Raised on the UI thread when a new search starts or the folder changes.</summary>
    public static event Action? Changed;

    /// <summary>The folder chosen in Settings, or null for the automatic search.</summary>
    public static string? ConfiguredDirectory => App.LoadSettings().PgToolsDirectory;

    /// <summary>The current search, started now if none has run this session.</summary>
    public static Task<PgToolScan> GetAsync() =>
        _fixed is { } fixedScan ? Task.FromResult(fixedScan) : _scan ??= Start();

    /// <summary>Searches again (Look Again, or after installing the programs).</summary>
    public static Task<PgToolScan> RescanAsync()
    {
        if (_fixed is { } fixedScan)
        {
            return Task.FromResult(fixedScan);
        }

        _scan = Start();
        Changed?.Invoke();
        return _scan;
    }

    /// <summary>Uses <paramref name="directory"/> from now on (null: search again) and searches.</summary>
    public static Task<PgToolScan> SetConfiguredDirectory(string? directory)
    {
        App.SetPgToolsDirectory(directory);
        return RescanAsync();
    }

    /// <summary>
    /// For the screenshot harness and the UI tests: every search answers
    /// <paramref name="scan"/> and nothing is run, so a frame never depends on
    /// what is installed on the machine rendering it. Null goes back to searching.
    /// </summary>
    public static void UseFixedScan(PgToolScan? scan)
    {
        _fixed = scan;
        _scan = null;
        Changed?.Invoke();
    }

    private static Task<PgToolScan> Start()
    {
        var configured = ConfiguredDirectory;
        return Task.Run(() => PgToolLocator.ScanAsync(configured, CancellationToken.None));
    }
}
