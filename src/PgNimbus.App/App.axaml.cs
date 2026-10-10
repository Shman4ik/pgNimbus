using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Nimbus.Ui.Fonts;
using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Backup;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Import;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Notifications;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Security;
using PgNimbus.Core.Settings;
using PgNimbus.Core.Text;

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

    /// <summary>Remembers the always-show-the-filter-bar toggle so it survives a restart.</summary>
    private static void PersistShowFilterBar(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { ShowFilterBar = value });

    /// <summary>Remembers the record-query-history preference so it survives a restart.</summary>
    private static void PersistRecordQueryHistory(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { RecordQueryHistory = value });

    /// <summary>Remembers the editor's auto-alias-tables toggle so it survives a restart.</summary>
    private static void PersistAutoAliasTables(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { AutoAliasTables = value });

    /// <summary>Remembers the safe-mode (stage &amp; review grid changes) toggle so it survives a restart.</summary>
    private static void PersistSafeModeEdits(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { SafeModeEdits = value });

    /// <summary>Remembers the editor's word-wrap toggle so it survives a restart.</summary>
    private static void PersistWordWrapEditor(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { WordWrapEditor = value });

    private static void PersistPlanTreeView(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { PlanTreeView = value });

    private static void PersistSpreadsheetSafeExport(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { SpreadsheetSafeExport = value });

    private static (KeywordCase, bool, bool) LoadCompletionSettings()
    {
        var settings = SettingsStore.Load();
        var keywordCase = settings.CompletionKeywordCase switch
        {
            "upper" => KeywordCase.Upper,
            "lower" => KeywordCase.Lower,
            _ => KeywordCase.AsTyped,
        };
        return (keywordCase, settings.CompletionAlwaysQualifyTables, settings.CompletionEnterAccepts);
    }

    private static void PersistCompletionSettings(KeywordCase keywordCase, bool alwaysQualifyTables, bool enterAccepts) =>
        SettingsStore.Save(SettingsStore.Load() with
        {
            CompletionKeywordCase = keywordCase switch
            {
                KeywordCase.Upper => "upper",
                KeywordCase.Lower => "lower",
                _ => "typed",
            },
            CompletionAlwaysQualifyTables = alwaysQualifyTables,
            CompletionEnterAccepts = enterAccepts,
        });

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

    private static readonly CompletionUsageStore CompletionUsageStore = new();
    private static readonly object CompletionUsageSaveLock = new();

    /// <summary>
    /// This connection's completion usage (what its user accepts, how often),
    /// written back after every accept — off the UI thread, one write at a time.
    /// Same <c>host/database</c> key as the workspace; a connection with no host
    /// ranks by the session's accepts alone.
    /// </summary>
    private static CompletionUsage LoadCompletionUsage(string? connectionKey)
    {
        if (connectionKey is null)
        {
            return new CompletionUsage();
        }

        var usage = new CompletionUsage(CompletionUsageStore.Load(connectionKey));
        usage.Changed += () =>
        {
            var entries = usage.Entries;
            _ = Task.Run(() =>
            {
                lock (CompletionUsageSaveLock)
                {
                    try
                    {
                        CompletionUsageStore.Save(connectionKey, entries);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Losing a ranking hint is not worth an error dialog.
                    }
                }
            });
        };
        return usage;
    }

    /// <summary>
    /// Remembers this connection's LISTEN channels. Scoped by the same
    /// <c>host/database</c> key as the workspace: channels belong to one
    /// application's event plumbing, not to the app.
    /// </summary>
    private static void PersistNotifyChannels(string connectionKey, IReadOnlyList<string> channels)
    {
        var settings = SettingsStore.Load();
        SettingsStore.Save(settings with
        {
            NotifyChannels = Core.Settings.NotifyChannels.With(settings, connectionKey, channels),
        });
    }

    /// <summary>Remembers the command palette's recent-.sql-files list so it survives a restart.</summary>
    private static void PersistRecentSqlFiles(IReadOnlyList<string> value) =>
        SettingsStore.Save(SettingsStore.Load() with { RecentSqlFiles = [.. value] });

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

    /// <summary>Persists the folder pg_dump and pg_restore run from (null: search for them).</summary>
    internal static void SetPgToolsDirectory(string? value) =>
        SettingsStore.Save(SettingsStore.Load() with { PgToolsDirectory = string.IsNullOrWhiteSpace(value) ? null : value });

    /// <summary>Remembers where the last backup went, so the next one is suggested there.</summary>
    internal static void PersistLastBackupFolder(string? value) =>
        SettingsStore.Save(SettingsStore.Load() with { LastBackupFolder = value });

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

    /// <summary>
    /// Applies the persisted typography to the application's resources (DESIGN.md rule
    /// 22): the interface face, the monospace face and the SQL editor's size. Every use
    /// site reads them as <c>DynamicResource</c>, so open windows follow a change.
    /// </summary>
    private static void ApplyFonts(AppSettings settings)
    {
        if (Current is not { } app)
        {
            return;
        }

        NimbusFonts.Apply(app.Resources, InterfaceFontFromString(settings.InterfaceFont), settings.CodeFont);
        app.Resources[QueryEditorPanel.EditorFontSizeKey] = QueryEditorPanel.ClampEditorFontSize(settings.EditorFontSize);
    }

    /// <summary>The face a stored <see cref="AppSettings.InterfaceFont"/> means on this platform ("auto" is the platform's default).</summary>
    public static InterfaceFont InterfaceFontFromString(string? value) => value?.ToLowerInvariant() switch
    {
        "system" => InterfaceFont.System,
        "inter" => InterfaceFont.Inter,
        _ => NimbusFonts.PlatformDefault,
    };

    /// <summary>Applies and persists the interface face chosen on the preferences page ("system"/"inter").</summary>
    internal static void SetInterfaceFont(string value) => SaveAndApplyFonts(SettingsStore.Load() with { InterfaceFont = value });

    /// <summary>Applies and persists the monospace face chosen on the preferences page (null: the bundled one).</summary>
    internal static void SetCodeFont(string? value) => SaveAndApplyFonts(SettingsStore.Load() with { CodeFont = value });

    /// <summary>Applies and persists the SQL editor's font size chosen on the preferences page.</summary>
    internal static void SetEditorFontSize(double value) =>
        SaveAndApplyFonts(SettingsStore.Load() with { EditorFontSize = QueryEditorPanel.ClampEditorFontSize(value) });

    private static void SaveAndApplyFonts(AppSettings settings)
    {
        SettingsStore.Save(settings);
        ApplyFonts(settings);
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
    /// of its own, so it needs a host; when there is no main window yet it falls back
    /// to the connection dialog, which hosts its own copy for exactly this reason.
    /// Without that fallback the menu item did nothing on the app's first screen —
    /// the one place a Mac user is most likely to reach for it, and the one place the
    /// ☰ menu (the in-window route, and on Windows and Linux the only one) is absent.
    /// "Settings…" below has no such fallback on purpose: preferences hang off a
    /// connected window's view model, so there is nothing for it to show.
    /// </summary>
    private void OnAboutMenuItemClicked(object? sender, EventArgs e)
    {
        if (ActiveMainViewModel() is { } main)
        {
            main.ShowAboutCommand.Execute(null);
            return;
        }

        if (ActiveWindow<ConnectionDialog>()?.DataContext is ConnectionDialogViewModel dialog)
        {
            dialog.IsAboutOpen = true;
        }
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
    /// Disabled while only the connection dialog is up (see
    /// <see cref="TrackAppMenuState"/>); preferences hang off a connected
    /// window's view model.
    /// </summary>
    private void OnSettingsMenuItemClicked(object? sender, EventArgs e) =>
        ActiveMainViewModel()?.ShowPreferencesCommand.Execute(null);

    /// <summary>
    /// The main window's view model an app-menu item should act on. Null while only
    /// the connection dialog is up — every item here but About then has nothing to do.
    /// </summary>
    private MainViewModel? ActiveMainViewModel() => ActiveWindow<MainWindow>()?.DataContext as MainViewModel;

    /// <summary>
    /// The open window of type <typeparamref name="T"/> an app-menu item should act on:
    /// the active one, or the first if none is (the app menu is global, windows are not).
    /// </summary>
    private T? ActiveWindow<T>() where T : Window
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return desktop.Windows.OfType<T>().FirstOrDefault(w => w.IsActive)
            ?? desktop.Windows.OfType<T>().FirstOrDefault();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Class handlers, so every editor and text box gets them without opting
        // in. Here rather than in OnFrameworkInitializationCompleted so the
        // headless tests (which never get a lifetime) run the same wiring.
        Platform.MacTextKeys.Install();
        Platform.EditorTypingUndo.Install();
        Nimbus.Ui.Controls.ToolTipHitTesting.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Catch exceptions thrown on the UI thread once the loop is running
        // (event handlers, async-void continuations, posted jobs) so they
        // surface in the crash window instead of taking the app down silently.
        Diagnostics.CrashReporter.AttachToDispatcher();

        TightenAppDataOnce();

        // Restore the saved light/dark choice before any window resolves its
        // ActualThemeVariant, so the first frame already paints in the right theme.
        ApplyPersistedTheme();

        // And in the chosen faces, so no window lays its text out twice.
        ApplyFonts(SettingsStore.Load());

        // Resolve Ctrl-vs-Cmd before any window builds its key bindings.
        Hotkeys.Initialize(SettingsStore.Load().HotkeyScheme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            KeepRunningWithNoWindowsOnMac(desktop);
            TrackAppMenuState();

            // macOS: quitting must not hand the process back to AppKit's exit()
            // — it aborts on the way out (see MacShutdown).
            MacShutdown.ExitProcessOnShutdown(desktop);

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
    /// Restricts the app data directory and the files already in it to the
    /// current user (0700/0600), once per launch and off the UI thread: what
    /// this version writes is created that way (<see cref="AppDataFile"/>), but
    /// everything an earlier version wrote sat at 0644 under a home directory
    /// that is often 0755, readable by every other local user (security audit
    /// 2026-09, finding 10). A handful of <c>chmod</c> calls; a failure goes to
    /// the crash log rather than the screen, since nothing the user can do in
    /// the app would fix it. No-op on Windows.
    /// </summary>
    private static void TightenAppDataOnce()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                AppDataFile.TightenExisting(AppDataPaths.GetRootDirectory());
            }
            catch (Exception e)
            {
                // Swallowed on purpose (a background task nobody awaits): the
                // app works either way, only less privately.
                PgNimbus.Core.Diagnostics.CrashLogger.LogCritical("Could not restrict the app data directory to the current user", e);
            }
        });
    }

    /// <summary>
    /// macOS only: closing the last window leaves the app running instead of
    /// quitting it. Closing a window and quitting an app are two different
    /// actions on macOS — the menu bar stays, the Dock icon stays, and clicking
    /// that icon brings a window back (<see cref="ActivationKind.Reopen"/>).
    /// Quitting is Cmd+Q / the app menu's Quit, which still works: the
    /// platform's quit request reaches
    /// <c>ClassicDesktopStyleApplicationLifetime</c> through its
    /// <c>ShutdownRequested</c> hook, which doesn't consult
    /// <see cref="ShutdownMode"/> — so does <c>Shutdown()</c>, which the crash
    /// reporter and the startup probe call directly.
    ///
    /// Windows and Linux keep Avalonia's default <c>OnLastWindowClose</c>: a
    /// windowless app in the background is a macOS idea, and elsewhere it would
    /// read as "close did nothing".
    /// </summary>
    private void KeepRunningWithNoWindowsOnMac(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    ReopenWindow(desktop);
                }
            };
        }
    }

    /// <summary>
    /// macOS only: puts right what Avalonia's standard app-menu block gets wrong
    /// (<see cref="MacAppMenu.FixStandardItems"/>), and keeps Settings… enabled
    /// only while there is a main window for it to open preferences on. The
    /// enabled state follows windows opening and closing, and is re-read when
    /// the menu opens; posted, so a closing window has left the lifetime's list
    /// before it is counted.
    /// </summary>
    private void TrackAppMenuState()
    {
        if (!OperatingSystem.IsMacOS() || NativeMenu.GetMenu(this) is not { } appMenu)
        {
            return;
        }

        MacAppMenu.FixStandardItems(appMenu, Name ?? "pgNimbus");

        void Update() => MacAppMenu.UpdateSettingsItem(appMenu, ActiveMainViewModel() is not null);
        void Post() => Avalonia.Threading.Dispatcher.UIThread.Post(Update);

        Window.WindowOpenedEvent.AddClassHandler<Window>((_, _) => Post());
        Window.WindowClosedEvent.AddClassHandler<Window>((_, _) => Post());
        appMenu.NeedsUpdate += (_, _) => Update();
        Update();
    }

    /// <summary>
    /// The Dock icon was clicked. A window that merely sat behind something else
    /// (or minimized) is what the user is asking for, so raise that; only when
    /// every window is gone does the app need a fresh entry point, and that's
    /// the connection dialog — the closed window's data source and SSH tunnel
    /// went with it (see <see cref="BuildMainWindow(NpgsqlDataSource, string?, SshTunnelLease?, ConnectionProfile?, string?, PgToolConnection?)"/>),
    /// so there is nothing to resurrect.
    /// </summary>
    private static void ReopenWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.Windows.Count > 0)
        {
            var existing = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.Windows[0];
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Show();
            existing.Activate();
            return;
        }

        var dialog = BuildConnectionDialog(desktop);
        desktop.MainWindow = dialog;
        dialog.Show();
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
    /// connects on its own if the user turned the preference on. A fourth caller
    /// is <see cref="ReopenWindow"/>, the macOS Dock-icon route back in.
    ///
    /// <c>ShutdownMode</c> is Avalonia's default <c>OnLastWindowClose</c> on
    /// Windows and Linux — the app keeps running until every window (whichever
    /// one that is) has closed — and <c>OnExplicitShutdown</c> on macOS, where
    /// closing the last window is not quitting (see
    /// <see cref="KeepRunningWithNoWindowsOnMac"/>).
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
            var mainWindow = BuildMainWindow(
                dataSource,
                accentColor,
                tunnel is null ? null : SshTunnelLease.Create(tunnel),
                viewModel.ConnectedProfile,
                viewModel.ConnectedPassword);
            if (viewModel.ConnectedProfileId is { } profileId)
            {
                ForgetSessionPasswordsOnClose(mainWindow, profileId);
            }

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
    /// Opens a main window on <paramref name="database"/> of the same server,
    /// connected the way the window that asked is: its profile with the
    /// database swapped (not saved; the connection list doesn't grow), its
    /// password, and another hold on its SSH tunnel, since signing in to the jump
    /// host again would need the SSH secret, which no window keeps. One real
    /// connection is opened first, as the connection dialog does, so a failure
    /// lands in the restore window that asked rather than in a broken window.
    /// </summary>
    private static async Task OpenDatabaseInNewWindowAsync(
        string database,
        string connectionString,
        PgToolConnection toolConnection,
        string? accentColor,
        SshTunnelLease? tunnel,
        ConnectionProfile? profile,
        string? password)
    {
        var lease = tunnel?.Acquire();
        NpgsqlDataSource? dataSource = null;
        try
        {
            MainWindow window;
            if (profile is not null)
            {
                var target = profile with { Database = database };
                dataSource = target.CreateDataSource(password, lease is null ? null : (lease.Tunnel.LocalHost, lease.Tunnel.LocalPort));
                await using (await dataSource.OpenConnectionAsync())
                {
                }

                window = BuildMainWindow(dataSource, accentColor, lease, target, password);
                ForgetSessionPasswordsOnClose(window, profile.Id);
            }
            else
            {
                // PGNIMBUS_CONN: the data source's string, which Npgsql hands
                // back without its password, with the database swapped.
                var builder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Database = database,
                    Password = toolConnection.Password,
                };
                dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
                await using (await dataSource.OpenConnectionAsync())
                {
                }

                window = BuildMainWindow(dataSource, accentColor, lease, toolConnection: toolConnection.ForDatabase(database));
            }

            dataSource = null;
            lease = null;
            window.Show();
        }
        finally
        {
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync();
            }

            lease?.Dispose();
        }
    }

    // The profile each open main window was connected with.
    private static readonly Dictionary<Window, Guid> WindowProfiles = [];

    /// <summary>
    /// When <paramref name="window"/> closes, drops its profile's session-only
    /// passwords (the ones the OS store refused) from the credential store's
    /// memory, unless another open window is still connected with that profile
    /// (security audit 2026-09, finding 18). Stored passwords are untouched.
    /// </summary>
    private static void ForgetSessionPasswordsOnClose(Window window, Guid profileId)
    {
        WindowProfiles[window] = profileId;
        window.Closed += (_, _) =>
        {
            WindowProfiles.Remove(window);
            if (WindowProfiles.ContainsValue(profileId))
            {
                return;
            }

            // Off the UI thread: the store's lock can be held by a native call
            // waiting on the OS (a Keychain or Secret Service unlock).
            var store = CredentialStore.Create();
            _ = Task.Run(() =>
            {
                foreach (var id in ConnectionDialogViewModel.CredentialIdsFor(profileId))
                {
                    store.Forget(id);
                }
            });
        };
    }

    /// <summary>
    /// The <c>PGNIMBUS_CONN</c> entry point: no dialog stands behind this one, so
    /// the data source is created here and stays lazy — a bad connection string
    /// surfaces as the window's first schema-tree error, which is the right place
    /// for it when nobody is sitting in a connect form.
    /// </summary>
    internal static MainWindow BuildMainWindow(string connectionString) =>
        // A profile forces standard_conforming_strings on in its Options and
        // reads value-type arrays with nullable elements; this string comes from
        // nowhere near a profile, so both are added here, or the literals the
        // app composes would parse differently on this one path (finding 13)
        // and an int[] holding a NULL would be unreadable.
        BuildMainWindow(
            NpgsqlDataSource.Create(ConnectionProfile.ForAppSession(connectionString)),
            toolConnection: PgToolConnection.FromConnectionString(connectionString));

    /// <summary>
    /// Builds a connected window around an existing <paramref name="dataSource"/>
    /// and takes ownership of it (and of its hold on <paramref name="tunnel"/>):
    /// both are released by the window's <c>Closed</c> handler, and the tunnel
    /// closes with the last window holding it. The connection dialog hands over a
    /// data source that has already opened one connection, so by the time this
    /// runs the credentials are known good.
    /// <paramref name="password"/> and the tunnel's local end become the
    /// connection pg_dump and pg_restore use (<see cref="PgToolConnection"/>),
    /// which <paramref name="toolConnection"/> gives directly for a window with
    /// no profile behind it.
    /// </summary>
    internal static MainWindow BuildMainWindow(
        NpgsqlDataSource dataSource,
        string? accentColor = null,
        SshTunnelLease? tunnel = null,
        ConnectionProfile? profile = null,
        string? password = null,
        PgToolConnection? toolConnection = null)
    {
        toolConnection ??= profile is null
            ? null
            : PgToolConnection.From(profile, password, tunnel is null ? null : (tunnel.Tunnel.LocalHost, tunnel.Tunnel.LocalPort));
        var connectionString = dataSource.ConnectionString;
        var engine = new QueryEngine(dataSource);
        var explainService = new ExplainService(dataSource);
        var schemaService = new SchemaService(dataSource);
        var schemaEditor = new SchemaEditor(dataSource);
        var ddlService = new DdlService(dataSource);
        var activityService = new ActivityService(dataSource);
        var databaseStatsService = new DatabaseStatsService(dataSource);
        var statementStatsService = new StatementStatsService(dataSource);
        var roleService = new RoleService(dataSource);
        var privilegeService = new PrivilegeService(dataSource);
        var securityEditor = new SecurityEditor(dataSource);
        var importService = new ImportService(dataSource);
        var schemaTree = new SchemaTreeViewModel(
            schemaService,
            SettingsStore.Load().ShowAdvancedSchemaObjects,
            PersistShowAdvancedSchemaObjects,
            SettingsStore.Load().ShowSchemaSizes,
            PersistShowSchemaSizes);
        var completionProvider = new SqlCompletionProvider(schemaService);

        var csb = new NpgsqlConnectionStringBuilder(connectionString);

        // Per-connection workspace key must match the label MainViewModel stamps
        // history with, so a workspace saved under one connection only ever
        // restores for that same host/database.
        var workspaceStore = new WorkspaceStore();
        var connectionHost = csb.Host ?? "";
        var connectionDatabase = csb.Database ?? "";
        var workspaceKey = string.IsNullOrEmpty(connectionHost) ? null : $"{connectionHost}/{connectionDatabase}";

        // Built after the connection key, which scopes its restored channels.
        var notifyMonitor = new NotifyMonitorViewModel(
            new NotificationListener(dataSource),
            channels: Core.Settings.NotifyChannels.For(SettingsStore.Load(), workspaceKey),
            persistChannels: workspaceKey is null ? null : channels => PersistNotifyChannels(workspaceKey, channels));

        var viewModel = new MainViewModel(
            engine, explainService, schemaTree, schemaService, schemaEditor, ddlService, completionProvider, notifyMonitor, activityService, databaseStatsService, statementStatsService, roleService, privilegeService, securityEditor, importService,
            accentColor,
            connectionHost: connectionHost,
            connectionDatabase: connectionDatabase,
            connectionName: profile?.Name,
            connectionEndpoint: profile is null ? null : MainViewModel.DescribeEndpoint(profile),
            // The server's own answer follows once the window is up
            // (DetectWriteStateAsync below); the profile's is known now.
            readOnlyConnection: csb.Options?.Contains(ConnectionProfile.ReadOnlySessionOption, StringComparison.Ordinal) == true,
            autoAliasTables: SettingsStore.Load().AutoAliasTables,
            persistAutoAliasTables: PersistAutoAliasTables,
            safeModeEdits: SettingsStore.Load().SafeModeEdits,
            persistSafeModeEdits: PersistSafeModeEdits,
            showFilterBar: SettingsStore.Load().ShowFilterBar,
            persistShowFilterBar: PersistShowFilterBar,
            recordQueryHistory: SettingsStore.Load().RecordQueryHistory,
            persistRecordQueryHistory: PersistRecordQueryHistory,
            wordWrapEditor: SettingsStore.Load().WordWrapEditor,
            persistWordWrapEditor: PersistWordWrapEditor,
            planTreeView: SettingsStore.Load().PlanTreeView,
            persistPlanTreeView: PersistPlanTreeView,
            spreadsheetSafeExport: SettingsStore.Load().SpreadsheetSafeExport,
            persistSpreadsheetSafeExport: PersistSpreadsheetSafeExport,
            workspace: workspaceKey is null ? null : workspaceStore.GetEntry(workspaceKey),
            recentSqlFiles: SettingsStore.Load().RecentSqlFiles,
            persistRecentSqlFiles: PersistRecentSqlFiles,
            // Per-connection, same key as the workspace. A connection with no
            // host (nothing to key on) still toggles exclusions for the session;
            // there's just nowhere to write them back to.
            excludedSchemas: AutocompleteExclusions.For(SettingsStore.Load(), workspaceKey),
            persistExcludedSchemas: workspaceKey is null ? null : schemas => PersistExcludedSchemas(workspaceKey, schemas),
            completionUsage: LoadCompletionUsage(workspaceKey),
            completionSettings: LoadCompletionSettings(),
            persistCompletionSettings: PersistCompletionSettings,
            backups: toolConnection is null ? null : new BackupService(dataSource, toolConnection),
            restores: toolConnection is null ? null : new RestoreService(dataSource, toolConnection));

        // "Open in New Window" after a restore into a new database: the same
        // profile and password, and the same SSH tunnel (another hold on it).
        if (toolConnection is not null)
        {
            var tools = toolConnection;
            viewModel.OpenDatabaseInNewWindow = database =>
                OpenDatabaseInNewWindowAsync(database, dataSource.ConnectionString, tools, accentColor, tunnel, profile, password);
        }

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        // Restore last session's window placement before the window shows, and
        // save it back on close - session state alongside the workspace restore.
        WindowPlacementPersistence.Attach(window, new WindowPlacementStore());

        window.Closed += async (_, _) =>
        {
            // A completion refresh still reading this connection's catalog stops
            // here, rather than running on against a pool about to be disposed
            // (this is also the "switch connection" path).
            completionProvider.Dispose();

            // Save the workspace before anything else tears down - a failed save
            // must never block window close / resource teardown. This fires on
            // both a normal app exit and a "switch connection" (which closes the
            // previous window), so the snapshot always files under the OLD
            // connection's key - exactly what per-connection scoping wants.
            try
            {
                if (workspaceKey is not null)
                {
                    var tabs = viewModel.Tabs.Select(t => t.ToWorkspaceTab()).ToList();
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
            // otherwise leaks on every "switch connection"), then this window's
            // hold on the SSH tunnel that carries all of it, which closes the
            // tunnel unless a window opened on a restored database still uses it.
            await notifyMonitor.DisposeAsync();
            await dataSource.DisposeAsync();
            tunnel?.Dispose();
        };

        _ = schemaTree.RefreshCommand.ExecuteAsync(null);
        _ = completionProvider.RefreshAsync(CancellationToken.None);
        _ = viewModel.DetectWriteStateAsync();

        return window;
    }
}
