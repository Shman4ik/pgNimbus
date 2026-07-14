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

    /// <summary>Remembers the editor's auto-alias-tables toggle so it survives a restart.</summary>
    private static void PersistAutoAliasTables(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { AutoAliasTables = value });

    /// <summary>Remembers the safe-mode (stage &amp; review grid changes) toggle so it survives a restart.</summary>
    private static void PersistSafeModeEdits(bool value) =>
        SettingsStore.Save(SettingsStore.Load() with { SafeModeEdits = value });

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
    /// "About pgNimbus" in the macOS app menu (see App.axaml). Opens the
    /// project page - version/license already live in the connection dialog's
    /// footer, so a dedicated About window would just duplicate them.
    /// </summary>
    private void OnAboutMenuItemClicked(object? sender, EventArgs e)
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
    /// Builds the connection-profile picker. Used both at startup (as
    /// <see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/>, no
    /// <paramref name="previousWindow"/>) and for "switch connection" from an
    /// already-open <see cref="MainWindow"/> — in that case, connecting closes
    /// <paramref name="previousWindow"/> after the new one is up, so its
    /// resources (notify-listen connection, SSH tunnel) tear down via its own
    /// <c>Closed</c> handler exactly as they would on a normal window close.
    /// </summary>
    internal static ConnectionDialog BuildConnectionDialog(IClassicDesktopStyleApplicationLifetime desktop, Window? previousWindow = null)
    {
        var viewModel = new ConnectionDialogViewModel(new ConnectionProfileStore(), CredentialStore.Create());
        var dialog = new ConnectionDialog { DataContext = viewModel };

        viewModel.Connected += (connectionString, accentColor, tunnel) =>
        {
            var mainWindow = BuildMainWindow(connectionString, accentColor, tunnel);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            dialog.Close();
            previousWindow?.Close();
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
        var importService = new ImportService(dataSource);
        var schemaTree = new SchemaTreeViewModel(
            schemaService,
            SettingsStore.Load().ShowAdvancedSchemaObjects,
            PersistShowAdvancedSchemaObjects);
        var completionProvider = new SqlCompletionProvider(schemaService);
        var notifyMonitor = new NotifyMonitorViewModel(new NotificationListener(dataSource));

        var csb = new NpgsqlConnectionStringBuilder(connectionString);

        var window = new MainWindow
        {
            DataContext = new MainViewModel(
                engine, explainService, schemaTree, schemaService, schemaEditor, ddlService, completionProvider, notifyMonitor, activityService, importService,
                accentColor,
                connectionHost: csb.Host ?? "",
                connectionDatabase: csb.Database ?? "",
                autoAliasTables: SettingsStore.Load().AutoAliasTables,
                persistAutoAliasTables: PersistAutoAliasTables,
                safeModeEdits: SettingsStore.Load().SafeModeEdits,
                persistSafeModeEdits: PersistSafeModeEdits),
        };

        window.Closed += async (_, _) =>
        {
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
