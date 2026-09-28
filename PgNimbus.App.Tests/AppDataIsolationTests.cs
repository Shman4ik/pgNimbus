using System.Runtime.CompilerServices;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Redirects the app data root before any test runs — see
/// <see cref="IsolatedAppData"/>. A module initializer, because the stores
/// resolve their path in their constructors and <c>App</c>'s are static: the
/// first test to touch the app must already find the redirect in place.
/// </summary>
internal static class AppDataIsolation
{
    [ModuleInitializer]
    internal static void Enable() => IsolatedAppData.Enable();
}

/// <summary>
/// Nothing a test does may reach the developer's real app data. On 2026-09-28
/// the owner's <c>saved-queries.json</c> was found holding exactly the fixture
/// list ("Revenue by day", "Slow orders", "Unshipped items", "Daily report"):
/// the fixture view model used the default stores, and <see cref="SaveQueryTests"/>
/// saved through them. These tests fail if any route back there reopens.
/// </summary>
public class AppDataIsolationTests
{
    [Test]
    public async Task The_app_data_root_is_redirected_for_the_whole_process()
    {
        // Covers every store that falls back to the root: settings, workspace,
        // completion usage, connection profiles, window placement, the crash log.
        var root = AppDataPaths.GetRootDirectory();

        await Assert.That(Full(root)).IsEqualTo(Full(IsolatedAppData.Directory));
        await Assert.That(IsUnder(root, AppDataPaths.GetDefaultRootDirectory())).IsFalse();
        await Assert.That(IsUnder(new SavedQueryStore().FilePath, AppDataPaths.GetDefaultRootDirectory())).IsFalse();
    }

    [Test]
    public async Task A_fixture_main_view_model_keeps_its_stores_out_of_the_real_app_data()
    {
        await Ui.Run(async () =>
        {
            var saved = Fixtures.MainWindowViewModel().SavedQueries;
            var realRoot = AppDataPaths.GetDefaultRootDirectory();

            foreach (var path in new[] { saved.SavedQueryStore.FilePath, saved.HistoryStore.FilePath })
            {
                await Assert.That(IsUnder(path, realRoot)).IsFalse();
                await Assert.That(IsUnder(path, IsolatedAppData.Directory)).IsTrue();
            }

            // And the writes land where the paths say, in a directory of this
            // view model's own: the list on disk is the one just saved, not the
            // leftovers of another test.
            saved.SaveQuery("Isolation check", "SELECT 1;");
            saved.RecordExecution(new QueryHistoryEntry("SELECT 1;", DateTimeOffset.UtcNow, 1.0, "1 row"));

            var written = new SavedQueryStore(saved.SavedQueryStore.FilePath).Load();
            await Assert.That(written.Select(q => q.Name)).Contains("Isolation check");
            await Assert.That(written).Count().IsEqualTo(saved.SavedQueries.Count);
            await Assert.That(File.Exists(saved.HistoryStore.FilePath)).IsTrue();
        });
    }

    [Test]
    public async Task Two_fixture_view_models_do_not_share_saved_queries()
    {
        await Ui.Run(async () =>
        {
            var first = Fixtures.MainWindowViewModel().SavedQueries;
            var second = Fixtures.MainWindowViewModel().SavedQueries;

            await Assert.That(second.SavedQueryStore.FilePath).IsNotEqualTo(first.SavedQueryStore.FilePath);
        });
    }

    /// <summary>
    /// <c>App</c>'s settings store is static and private, built the first time
    /// the app is touched; the Preferences page writes through it. Writing a
    /// preference and finding it in the redirected settings file proves that
    /// store, too, was built after the redirect.
    /// </summary>
    [Test]
    public async Task Preferences_write_to_the_redirected_settings_file()
    {
        await Ui.Run(async () =>
        {
            var preferences = new PreferencesViewModel(Fixtures.MainWindowViewModel());
            var settingsFile = Path.Combine(IsolatedAppData.Directory, "settings.json");

            preferences.AutoConnectLastProfile = true;
            try
            {
                await Assert.That(File.Exists(settingsFile)).IsTrue();
                await Assert.That(new PgNimbus.Core.Settings.AppSettingsStore(settingsFile).Load().AutoConnectLastProfile).IsTrue();
            }
            finally
            {
                preferences.AutoConnectLastProfile = false;
            }
        });
    }

    private static string Full(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Case-insensitively, since macOS and Windows file systems are.</summary>
    private static bool IsUnder(string path, string root)
    {
        var full = Full(path);
        var fullRoot = Full(root);
        return string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
