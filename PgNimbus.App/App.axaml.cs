using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Notifications;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ConnectionDialog BuildConnectionDialog(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var viewModel = new ConnectionDialogViewModel(new ConnectionProfileStore(), CredentialStore.Create());
        var dialog = new ConnectionDialog { DataContext = viewModel };

        viewModel.Connected += (connectionString, accentColor, tunnel) =>
        {
            var mainWindow = BuildMainWindow(connectionString, accentColor, tunnel);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            dialog.Close();
        };

        return dialog;
    }

    private static MainWindow BuildMainWindow(string connectionString, string? accentColor = null, SshTunnel? tunnel = null)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var engine = new QueryEngine(dataSource);
        var explainService = new ExplainService(dataSource);
        var schemaService = new SchemaService(dataSource);
        var schemaEditor = new SchemaEditor(dataSource);
        var ddlService = new DdlService(dataSource);
        var schemaTree = new SchemaTreeViewModel(schemaService);
        var completionProvider = new SqlCompletionProvider(schemaService);
        var notifyMonitor = new NotifyMonitorViewModel(new NotificationListener(dataSource));

        var csb = new NpgsqlConnectionStringBuilder(connectionString);

        var window = new MainWindow
        {
            DataContext = new MainViewModel(
                engine, explainService, schemaTree, schemaService, schemaEditor, ddlService, completionProvider, notifyMonitor,
                accentColor,
                connectionHost: csb.Host ?? "",
                connectionDatabase: csb.Database ?? ""),
        };

        window.Closed += (_, _) =>
        {
            _ = notifyMonitor.DisposeAsync();
            tunnel?.Dispose();
        };

        _ = schemaTree.RefreshCommand.ExecuteAsync(null);
        _ = completionProvider.RefreshAsync(CancellationToken.None);

        return window;
    }
}
