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

    /// <summary>
    /// Remembers which schemas this connection keeps out of autocomplete. Scoped
    /// by the same <c>host/database</c> key the workspace snapshot uses —
    /// schema names are a property of one database, not of the app.
    /// </summary>
    private static void PersistExcludedSchemas(string connectionKey, IReadOnlyList<string> schemas)
    {
        var settings = SettingsStore.Load();
        SettingsStore.Save(settings with
        {
            AutocompleteExcludedSchemas = AutocompleteExclusions.With(settings, connectionKey, schemas),
        });
    }

    /// <summary>Remembers the command palette's recent-.sql-files list so it survives a restart.</summary>
    private static void PersistRecentSqlFiles(IReadOnlyList<string> value) =>
        SettingsStore.Save(SettingsStore.Load() with { RecentSqlFiles = value.ToList() });

    /// <summary>
    /// Remembers which saved profile was last connected to, so the next
    /// connection dialog opens with it preselected (and startup can connect to
    /// it outright when the preference is on). Null for an unsaved, ad-hoc
    /// connection — there is no profile to come back to.
    /// </summary>
    private static void PersistLastConnectionProfileId(Guid? value) =>
        SettingsStore.Save(SettingsStore.Load() with { LastConnectionProfileId = value?.ToString() });

    /// <summary>Applies the "connect to the last connection on startup" preference.</summary>
    internal static void SetAutoConnectLastProfile(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { AutoConnectLastProfile = value });

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

    /// <summary>
    /// "About pgNimbus" in the macOS app menu (see App.axaml): opens the About panel
    /// on the active main window (or the first one — the app menu is global, windows
    /// are not). The box is an <c>OverlayPanel</c> over a window rather than a window
    /// of its own now, so like "Settings…" below this is a no-op while only the
    /// connection dialog is up; the ☰ menu is the in-window route, and on Windows and
    /// Linux the only one.
    /// </summary>
    private void OnAboutMenuItemClicked(object? sender, EventArgs e) =>
        ActiveMainViewModel()?.ShowAboutCommand.Execute(null);

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
    private void OnSettingsMenuItemClicked(object? sender, EventArgs e) =>
        ActiveMainViewModel()?.ShowPreferencesCommand.Execute(null);

    /// <summary>
    /// The main window an app-menu item should act on: the active one, or the first
    /// if none is (the app menu is global, windows are not). Null while only the
    /// connection dialog is up — nothing in this menu has a window-less meaning.
    /// </summary>
    private MainViewModel? ActiveMainViewModel()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var window = desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive)
            ?? desktop.Windows.OfType<MainWindow>().FirstOrDefault();
        return window?.DataContext as MainViewModel;
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
        // Catch exceptions thrown on the UI thread once the loop is running
        // (event handlers, async-void continuations, posted jobs) so they
        // surface in the crash window instead of taking the app down silently.
        Diagnostics.CrashReporter.AttachToDispatcher();

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
                desktop.MainWindow = BuildConnectionDialog(desktop, autoConnect: true);
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
    /// — untouched; the new window simply joins them. Only the startup flow
    /// passes <paramref name="autoConnect"/>, and even then the dialog only
    /// connects on its own if the user turned the preference on.
    /// There is no explicit
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
    internal static ConnectionDialog BuildConnectionDialog(IClassicDesktopStyleApplicationLifetime desktop, Window? previousWindow = null, bool replaceMainWindow = true, bool autoConnect = false)
    {
        var settings = SettingsStore.Load();
        var lastProfileId = Guid.TryParse(settings.LastConnectionProfileId, out var parsed) ? parsed : (Guid?)null;

        var viewModel = new ConnectionDialogViewModel(
            new ConnectionProfileStore(),
            CredentialStore.Create(),
            lastProfileId,
            PersistLastConnectionProfileId)
        {
            // Only startup auto-connects; reaching this dialog from an open
            // window ("switch connection", "new window") means the user came
            // here to pick something, so connecting out from under them would
            // be exactly wrong.
            AutoConnectOnOpen = autoConnect && settings.AutoConnectLastProfile,
        };

        var dialog = new ConnectionDialog { DataContext = viewModel };
        WindowPlacementPersistence.Attach(dialog, WindowPlacementStore.ForConnectionDialog());

        viewModel.Connected += (dataSource, accentColor, tunnel) =>
        {
            var mainWindow = BuildMainWindow(dataSource, accentColor, tunnel);
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

    /// <summary>
    /// The <c>PGNIMBUS_CONN</c> entry point: no dialog stands behind this one, so
    /// the data source is created here and stays lazy — a bad connection string
    /// surfaces as the window's first schema-tree error, which is the right place
    /// for it when nobody is sitting in a connect form.
    /// </summary>
    internal static MainWindow BuildMainWindow(string connectionString) =>
        BuildMainWindow(NpgsqlDataSource.Create(connectionString));

    /// <summary>
    /// Builds a connected window around an existing <paramref name="dataSource"/>
    /// and takes ownership of it (and of <paramref name="tunnel"/>): both are
    /// disposed by the window's <c>Closed</c> handler. The connection dialog
    /// hands over a data source that has already opened one connection, so by
    /// the time this runs the credentials are known good.
    /// </summary>
    internal static MainWindow BuildMainWindow(NpgsqlDataSource dataSource, string? accentColor = null, SshTunnel? tunnel = null)
    {
        var connectionString = dataSource.ConnectionString;
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
            persistRecentSqlFiles: PersistRecentSqlFiles,
            // Per-connection, same key as the workspace. A connection with no
            // host (nothing to key on) still toggles exclusions for the session;
            // there's just nowhere to write them back to.
            excludedSchemas: AutocompleteExclusions.For(SettingsStore.Load(), workspaceKey),
            persistExcludedSchemas: workspaceKey is null ? null : schemas => PersistExcludedSchemas(workspaceKey, schemas));

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
