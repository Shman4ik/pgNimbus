using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Import;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Notifications;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Settings;

namespace PgNimbus.App;

public partial class App : Application
{
    private static readonly AppSettingsStore SettingsStore = new();

    /// <summary>
    /// Applies the theme the user last chose (see <see cref="PersistTheme"/>).
    /// A fresh install has no saved theme, which maps to <see cref="ThemeVariant.Default"/>
    /// — i.e. follow the OS, the pre-persistence behaviour.
    /// </summary>
    private void ApplyPersistedTheme() =>
        RequestedThemeVariant = ThemeFromString(SettingsStore.Load().Theme);

    /// <summary>Remembers an explicit light/dark choice so it survives a restart.</summary>
    internal static void PersistTheme(ThemeVariant variant) =>
        SettingsStore.Save(SettingsStore.Load() with { Theme = ThemeToString(variant) });

    /// <summary>Remembers the sidebar's advanced-objects toggle so it survives a restart.</summary>
    private static void PersistShowAdvancedSchemaObjects(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { ShowAdvancedSchemaObjects = value });

    /// <summary>Remembers the sidebar's show-sizes toggle so it survives a restart.</summary>
    private static void PersistShowSchemaSizes(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { ShowSchemaSizes = value });

    /// <summary>Remembers the editor's auto-alias-tables toggle so it survives a restart.</summary>
    private static void PersistAutoAliasTables(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { AutoAliasTables = value });

    /// <summary>Remembers the safe-mode (stage &amp; review grid changes) toggle so it survives a restart.</summary>
    private static void PersistSafeModeEdits(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { SafeModeEdits = value });

    /// <summary>Remembers the editor's word-wrap toggle so it survives a restart.</summary>
    private static void PersistWordWrapEditor(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { WordWrapEditor = value });

    /// <summary>Remembers the command palette's recent-.sql-files list so it survives a restart.</summary>
    private static void PersistRecentSqlFiles(IReadOnlyList<string> value) =>
        SettingsStore.Save(SettingsStore.Load() with { RecentSqlFiles = value.ToList() });

    /// <summary>The saved settings snapshot, for the preferences page to initialize from.</summary>
    internal static AppSettings LoadSettings() => SettingsStore.Load();

    /// <summary>Applies and persists a theme chosen on the preferences page ("system"/"light"/"dark").</summary>
    internal static void SetTheme(string theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = ThemeFromString(theme);
        }

        SettingsStore.Save(SettingsStore.Load() with { Theme = theme });
    }

    /// <summary>Persists the hotkey scheme and re-resolves the live command modifier (see <see cref="Hotkeys"/>).</summary>
    internal static void SetHotkeyScheme(string scheme)
    {
        SettingsStore.Save(SettingsStore.Load() with { HotkeyScheme = scheme });
        Hotkeys.Initialize(scheme);
    }

    private static ThemeVariant ThemeFromString(string? theme) => theme?.ToLowerInvariant() switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private static string ThemeToString(ThemeVariant variant) =>
        variant == ThemeVariant.Dark ? "dark" : variant == ThemeVariant.Light ? "light" : "system";

    // The About box is app-global; one instance at a time, re-activated if
    // the menu item is clicked while it's already open.
    private AboutWindow? _aboutWindow;

    /// <summary>
    /// "About pgNimbus" in the macOS app menu (see App.axaml): the standard
    /// About box — name, version, license (<see cref="AboutWindow"/>).
    /// </summary>
    private void OnAboutMenuItemClicked(object? sender, EventArgs e)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    /// <summary>"pgNimbus on GitHub" in the macOS app menu: opens the project page.</summary>
    private void OnGitHubMenuItemClicked(object? sender, EventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Shman4ik/pgNimbus") { UseShellExecute = true });
        }
        catch
        {
            // No browser to hand off to is not worth crashing the app menu.
        }
    }

    /// <summary>
    /// "Settings…" in the macOS app menu: opens preferences for the active
    /// main window (or the first one — the app menu is global, windows aren't).
    /// A no-op while only the connection dialog is up; preferences hang off a
    /// connected window's view model.
    /// </summary>
    private void OnSettingsMenuItemClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var window = desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive)
            ?? desktop.Windows.OfType<MainWindow>().FirstOrDefault();
        (window?.DataContext as MainViewModel)?.ShowPreferencesCommand.Execute(null);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // DevTools are attached once, via .WithDeveloperTools() on the AppBuilder
        // in Program.cs (the MCP discovery hook documented in CLAUDE.md).
        // Attaching again here throws "already been attached" and crashes Debug
        // startup, so it must not be duplicated.
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Restore the saved light/dark choice before any window resolves its
        // ActualThemeVariant, so the first frame already paints in the right theme.
        ApplyPersistedTheme();

        // Resolve Ctrl-vs-Cmd before any window builds its key bindings.
        Hotkeys.Initialize(SettingsStore.Load().HotkeyScheme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var envConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_CONN");
            if (!string.IsNullOrWhiteSpace(envConnectionString))
            {
                // PGNIMBUS_CONN accepts any format the connection dialog does
                // (postgres:// URI, JDBC, libpq keywords, ...), not just
                // Npgsql Key=Value.
                desktop.MainWindow = BuildMainWindow(ConnectionStringParser.NormalizeToNpgsql(envConnectionString));
            }
            else
            {
                desktop.MainWindow = BuildConnectionDialog(desktop);
            }

            StartupProbe.ArmIfRequested(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the connection-profile picker. Used for three flows:
    /// startup (as <see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/>,
    /// no <paramref name="previousWindow"/>, default <paramref name="replaceMainWindow"/>),
    /// "switch connection" from an already-open <see cref="MainWindow"/> (which
    /// passes itself as <paramref name="previousWindow"/> and keeps the default
    /// <paramref name="replaceMainWindow"/> so connecting closes it after the new
    /// window is up, tearing its resources down via its own <c>Closed</c> handler
    /// exactly as a normal window close would), and "open connection in new
    /// window" (<paramref name="replaceMainWindow"/> = false, no
    /// <paramref name="previousWindow"/>) which leaves every existing window —
    /// and <see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/> itself
    /// — untouched; the new window simply joins them. There is no explicit
    /// <c>ShutdownMode</c> set, so Avalonia's default <c>OnLastWindowClose</c>
    /// applies: the app keeps running until every window (whichever one that is)
    /// has closed.
    ///
    /// Note: two windows connected to the same host/database each own a
    /// separate in-memory workspace snapshot but save under the same
    /// per-connection key on close (see <see cref="BuildMainWindow"/>) — the
    /// one that closes last wins and overwrites the other's save. Acceptable
    /// by design; not worth merging snapshots across windows for.
    /// </summary>
    internal static ConnectionDialog BuildConnectionDialog(IClassicDesktopStyleApplicationLifetime desktop, Window? previousWindow = null, bool replaceMainWindow = true)
    {
        var viewModel = new ConnectionDialogViewModel(new ConnectionProfileStore(), CredentialStore.Create());
        var dialog = new ConnectionDialog { DataContext = viewModel };

        viewModel.Connected += (connectionString, accentColor, tunnel) =>
        {
            var mainWindow = BuildMainWindow(connectionString, accentColor, tunnel);
            mainWindow.Show();
            dialog.Close();

            if (replaceMainWindow)
            {
                desktop.MainWindow = mainWindow;
                previousWindow?.Close();
            }
        };

        return dialog;
    }

    internal static MainWindow BuildMainWindow(string connectionString, string? accentColor = null, SshTunnel? tunnel = null)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var engine = new QueryEngine(dataSource);
        var explainService = new ExplainService(dataSource);
        var schemaService = new SchemaService(dataSource);
        var schemaEditor = new SchemaEditor(dataSource);
        var ddlService = new DdlService(dataSource);
        var activityService = new ActivityService(dataSource);
        var databaseStatsService = new DatabaseStatsService(dataSource);
        var importService = new ImportService(dataSource);
        var schemaTree = new SchemaTreeViewModel(
            schemaService,
            SettingsStore.Load().ShowAdvancedSchemaObjects,
            PersistShowAdvancedSchemaObjects,
            SettingsStore.Load().ShowSchemaSizes,
            PersistShowSchemaSizes);
        var completionProvider = new SqlCompletionProvider(schemaService);
        var notifyMonitor = new NotifyMonitorViewModel(new NotificationListener(dataSource));

        var csb = new NpgsqlConnectionStringBuilder(connectionString);

        // Per-connection workspace key must match the label MainViewModel stamps
        // history with, so a workspace saved under one connection only ever
        // restores for that same host/database.
        var workspaceStore = new WorkspaceStore();
        var connectionHost = csb.Host ?? "";
        var connectionDatabase = csb.Database ?? "";
        var workspaceKey = string.IsNullOrEmpty(connectionHost) ? null : $"{connectionHost}/{connectionDatabase}";

        var viewModel = new MainViewModel(
            engine, explainService, schemaTree, schemaService, schemaEditor, ddlService, completionProvider, notifyMonitor, activityService, databaseStatsService, importService,
            accentColor,
            connectionHost: connectionHost,
            connectionDatabase: connectionDatabase,
            autoAliasTables: SettingsStore.Load().AutoAliasTables,
            persistAutoAliasTables: PersistAutoAliasTables,
            safeModeEdits: SettingsStore.Load().SafeModeEdits,
            persistSafeModeEdits: PersistSafeModeEdits,
            wordWrapEditor: SettingsStore.Load().WordWrapEditor,
            persistWordWrapEditor: PersistWordWrapEditor,
            workspace: workspaceKey is null ? null : workspaceStore.GetEntry(workspaceKey),
            recentSqlFiles: SettingsStore.Load().RecentSqlFiles,
            persistRecentSqlFiles: PersistRecentSqlFiles);

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        // Restore last session's window placement before the window shows, and
        // save it back on close - session state alongside the workspace restore.
        WindowPlacementPersistence.Attach(window, new WindowPlacementStore());

        window.Closed += async (_, _) =>
        {
            // Save the workspace before anything else tears down - a failed save
            // must never block window close / resource teardown. This fires on
            // both a normal app exit and a "switch connection" (which closes the
            // previous window), so the snapshot always files under the OLD
            // connection's key - exactly what per-connection scoping wants.
            try
            {
                if (workspaceKey is not null)
                {
                    var tabs = viewModel.Tabs.Select(t => new WorkspaceTab(t.Sql, t.TitleOverride, t.FilePath)).ToList();
                    var activeIndex = Math.Max(viewModel.Tabs.IndexOf(viewModel.ActiveTab), 0);
                    workspaceStore.Save(workspaceKey, tabs, activeIndex);
                }
            }
            catch
            {
                // Losing the workspace snapshot is not worth blocking shutdown over.
            }

            // Order matters: drain the notify listener's connection back to the
            // pool first, then dispose the data source (the pool itself, which
            // otherwise leaks on every "switch connection"), then the SSH tunnel
            // that carries all of it.
            await notifyMonitor.DisposeAsync();
            await dataSource.DisposeAsync();
            tunnel?.Dispose();
        };

        _ = schemaTree.RefreshCommand.ExecuteAsync(null);
        _ = completionProvider.RefreshAsync(CancellationToken.None);

        return window;
    }
}
