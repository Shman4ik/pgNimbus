using PgNimbus.Core.Connections;
using PgNimbus.Core.Diagnostics;
using PgNimbus.Core.Query;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Tests.Settings;

/// <summary>
/// <see cref="AppDataFile"/>: the atomic replace, the corrupt-file backup, and
/// (on Linux and macOS only) the owner-only modes of security audit 2026-09,
/// finding 10. The mode tests skip on Windows, where <c>%AppData%</c> is per
/// user already and the Unix mode APIs throw.
/// </summary>
public class AppDataFileTests
{
    private const string WindowsSkipReason =
        "Unix file modes do not exist on Windows; %AppData% is per user already.";

    private const UnixFileMode Mode0644 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode Mode0755 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    internal static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "pgnimbus-appdatafile-tests", Guid.NewGuid().ToString("N"));

    internal static ConnectionProfile Profile(string name) =>
        new(Guid.NewGuid(), name, "localhost", 5432, "postgres", "postgres", SslMode.Prefer);

    [Test]
    public async Task WriteAllText_replaces_the_file_and_leaves_no_temp_file_behind()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            AppDataFile.WriteAllText(path, "first");
            AppDataFile.WriteAllText(path, "second");

            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("second");
            await Assert.That(Directory.GetFiles(dir).Select(p => Path.GetFileName(p))).IsEquivalentTo(["settings.json"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteAllText_writes_utf8_without_a_byte_order_mark()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "a.json");
        try
        {
            AppDataFile.WriteAllText(path, "é");

            await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 0xC3, 0xA9 }, CollectionOrdering.Matching);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task A_failed_replace_cleans_up_its_temp_file_and_throws()
    {
        var dir = NewTempDir();
        // The target is a directory, so the final rename fails on every platform.
        var path = Path.Combine(dir, "blocked");
        Directory.CreateDirectory(path);
        try
        {
            await Assert.That(() => AppDataFile.WriteAllText(path, "x")).Throws<Exception>();

            await Assert.That(Directory.Exists(path)).IsTrue();
            await Assert.That(Directory.GetFiles(dir)).IsEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task A_null_path_reads_nothing_and_drops_writes()
    {
        AppDataFile.WriteAllText(null, "x");
        AppDataFile.AppendAllText(null, "x");
        AppDataFile.WriteJson(null, new AppSettings(), AppSettingsJsonContext.Default.AppSettings);

        await Assert.That(AppDataFile.ReadJson(null, AppSettingsJsonContext.Default.AppSettings)).IsNull();
    }

    [Test]
    public async Task An_unparseable_file_is_moved_aside_before_the_store_starts_over()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "connections.json");
        Directory.CreateDirectory(dir);
        const string torn = "[{\"Id\":\"8b1c";
        await File.WriteAllTextAsync(path, torn);
        try
        {
            var store = new ConnectionProfileStore(path);

            await Assert.That(store.Load()).IsEmpty();

            // The torn file is kept, byte for byte, beside where it was...
            var backups = Directory.GetFiles(dir, "connections.json.corrupt-*");
            await Assert.That(backups).Count().IsEqualTo(1);
            await Assert.That(await File.ReadAllTextAsync(backups[0])).IsEqualTo(torn);
            await Assert.That(File.Exists(path)).IsFalse();

            // ...so the next autosave writes a new file instead of over the only copy.
            store.Save([Profile("local")]);
            await Assert.That(store.Load().Select(p => p.Name)).IsEquivalentTo(["local"]);
            await Assert.That(await File.ReadAllTextAsync(backups[0])).IsEqualTo(torn);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Two_backups_in_the_same_second_do_not_overwrite_each_other()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "history.json");
        try
        {
            await File.WriteAllTextAsync(path, "one");
            var first = AppDataFile.BackUpCorrupt(path) ?? throw new InvalidOperationException("first backup failed");
            await File.WriteAllTextAsync(path, "two");
            var second = AppDataFile.BackUpCorrupt(path) ?? throw new InvalidOperationException("second backup failed");

            await Assert.That(second).IsNotEqualTo(first);
            await Assert.That(await File.ReadAllTextAsync(first)).IsEqualTo("one");
            await Assert.That(await File.ReadAllTextAsync(second)).IsEqualTo("two");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task A_missing_file_is_neither_backed_up_nor_created_by_a_read()
    {
        var dir = NewTempDir();
        await Assert.That(new QueryHistoryStore(Path.Combine(dir, "history.json")).Load()).IsEmpty();
        await Assert.That(Directory.Exists(dir)).IsFalse();
    }

    [Test]
    public async Task On_unix_a_written_file_is_0600_and_its_new_directories_0700()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test(WindowsSkipReason);
            return;
        }

        var dir = NewTempDir();
        var path = Path.Combine(dir, "nested", "workspace.json");
        try
        {
            AppDataFile.WriteAllText(path, "{}");

            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(AppDataFile.PrivateFileMode);
            await Assert.That(File.GetUnixFileMode(Path.Combine(dir, "nested"))).IsEqualTo(AppDataFile.PrivateDirectoryMode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task On_unix_replacing_a_0644_file_leaves_it_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test(WindowsSkipReason);
            return;
        }

        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "history.json");
        try
        {
            await File.WriteAllTextAsync(path, "[]");
            File.SetUnixFileMode(path, Mode0644); // what earlier versions wrote

            AppDataFile.WriteAllText(path, "[ ]");

            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(AppDataFile.PrivateFileMode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task On_unix_the_crash_log_is_created_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test(WindowsSkipReason);
            return;
        }

        var dir = NewTempDir();
        try
        {
            var log = new CrashLog(Path.Combine(dir, "logs"));
            var written = log.LogCritical("mode check", null) ?? throw new InvalidOperationException("nothing was logged");

            await Assert.That(File.GetUnixFileMode(written)).IsEqualTo(AppDataFile.PrivateFileMode);
            await Assert.That(File.GetUnixFileMode(Path.Combine(dir, "logs"))).IsEqualTo(AppDataFile.PrivateDirectoryMode);

            // A second entry appends to the same private file.
            log.LogCritical("second", null);
            await Assert.That(File.GetUnixFileMode(written)).IsEqualTo(AppDataFile.PrivateFileMode);
            await Assert.That(await File.ReadAllTextAsync(written)).Contains("second");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task On_unix_an_existing_directory_outside_the_app_data_root_is_left_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test(WindowsSkipReason);
            return;
        }

        // A store handed an explicit path (a test writing into /tmp) must never
        // chmod a directory the app does not own.
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, Mode0755);
        try
        {
            AppDataFile.WriteAllText(Path.Combine(dir, "a.json"), "{}");

            await Assert.That(File.GetUnixFileMode(dir)).IsEqualTo(Mode0755);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task On_unix_TightenExisting_restricts_what_an_earlier_version_left_readable()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test(WindowsSkipReason);
            return;
        }

        var root = NewTempDir();
        var logs = Path.Combine(root, "logs");
        var credentials = Path.Combine(root, "credentials");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(credentials);
        var files = new[]
        {
            Path.Combine(root, "connections.json"),
            Path.Combine(root, "history.json"),
            Path.Combine(logs, "pgnimbus.log"),
            Path.Combine(credentials, "legacy.cred"),
        };
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(file, "x");
            File.SetUnixFileMode(file, Mode0644);
        }

        foreach (var directory in new[] { root, logs, credentials })
        {
            File.SetUnixFileMode(directory, Mode0755);
        }

        try
        {
            AppDataFile.TightenExisting(root);

            foreach (var file in files)
            {
                await Assert.That(File.GetUnixFileMode(file)).IsEqualTo(AppDataFile.PrivateFileMode);
            }

            foreach (var directory in new[] { root, logs, credentials })
            {
                await Assert.That(File.GetUnixFileMode(directory)).IsEqualTo(AppDataFile.PrivateDirectoryMode);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TightenExisting_is_a_no_op_for_a_missing_or_null_root()
    {
        var missing = NewTempDir();

        AppDataFile.TightenExisting(null);
        AppDataFile.TightenExisting(missing);

        await Assert.That(Directory.Exists(missing)).IsFalse();
    }
}

/// <summary>
/// The "no app data directory at all" state: when neither
/// <c>ApplicationData</c> nor <c>HOME</c> resolves, the app used to write into
/// the OS temp directory, on Linux the shared <c>/tmp</c> where another user
/// can pre-create <c>pgNimbus/</c> (security audit 2026-09, finding 10). Now
/// every store runs from memory. Not parallel: the seam is process-wide.
/// </summary>
[NotInParallel(nameof(AppDataPaths))]
public class NoAppDataRootTests
{
    [Test]
    public async Task No_application_data_and_no_home_resolves_to_no_root_rather_than_temp()
    {
        await Assert.That(AppDataPaths.ResolveDefaultRoot(null, null)).IsNull();
        await Assert.That(AppDataPaths.ResolveDefaultRoot("", "")).IsNull();
        await Assert.That(AppDataPaths.ResolveDefaultRoot("", "/home/ann"))
            .IsEqualTo(Path.Combine("/home/ann", ".config", "pgNimbus"));
        await Assert.That(AppDataPaths.ResolveDefaultRoot("/home/ann/.config", null))
            .IsEqualTo(Path.Combine("/home/ann/.config", "pgNimbus"));
    }

    [Test]
    public async Task With_no_root_every_store_keeps_nothing_and_nothing_throws()
    {
        AppDataPaths.RootResolverForTests = () => null;
        try
        {
            await Assert.That(AppDataPaths.GetRootDirectory()).IsNull();
            await Assert.That(AppDataPaths.Resolve("settings.json")).IsNull();

            var profiles = new ConnectionProfileStore();
            profiles.Save([AppDataFileTests.Profile("local")]);
            await Assert.That(profiles.Load()).IsEmpty();

            var settings = new AppSettingsStore();
            settings.Save(new AppSettings { AutoConnectLastProfile = true });
            await Assert.That(settings.Load().AutoConnectLastProfile).IsFalse();

            var history = new QueryHistoryStore();
            history.Append(new QueryHistoryEntry("SELECT 1;", DateTimeOffset.UtcNow, 1.0, "1 row"));
            await Assert.That(history.FilePath).IsNull();
            await Assert.That(history.Load()).IsEmpty();

            var saved = new SavedQueryStore();
            saved.Save([new SavedQuery(Guid.NewGuid(), "q", "SELECT 1;", DateTimeOffset.UtcNow)]);
            await Assert.That(saved.FilePath).IsNull();
            await Assert.That(saved.Load()).IsEmpty();

            var workspace = new WorkspaceStore();
            workspace.Save("localhost/demo", [new WorkspaceTab("SELECT 1;")], 0);
            await Assert.That(workspace.GetEntry("localhost/demo")).IsNull();

            var placement = new WindowPlacementStore();
            placement.Save(new WindowPlacement(0, 0, 800, 600, false));
            await Assert.That(placement.Load()).IsNull();
            await Assert.That(WindowPlacementStore.ForConnectionDialog().Load()).IsNull();

            var usage = new CompletionUsageStore();
            usage.Save("localhost/demo", []);
            await Assert.That(usage.Load("localhost/demo")).IsEmpty();

            var log = new CrashLog(AppDataPaths.Resolve("logs"));
            await Assert.That(log.FilePath).IsNull();
            await Assert.That(log.LogCritical("nowhere to write", new InvalidOperationException("boom"))).IsNull();
        }
        finally
        {
            AppDataPaths.RootResolverForTests = null;
        }
    }
}
