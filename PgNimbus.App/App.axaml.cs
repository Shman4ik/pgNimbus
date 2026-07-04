using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;
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
                desktop.MainWindow = BuildMainWindow(envConnectionString);
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

        viewModel.Connected += (connectionString, tunnel) =>
        {
            var mainWindow = BuildMainWindow(connectionString, tunnel);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            dialog.Close();
        };

        return dialog;
    }

    private static MainWindow BuildMainWindow(string connectionString, SshTunnel? tunnel = null)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var engine = new QueryEngine(dataSource);
        var explainService = new ExplainService(dataSource);
        var schemaService = new SchemaService(dataSource);
        var schemaTree = new SchemaTreeViewModel(schemaService);
        var completionProvider = new SqlCompletionProvider(schemaService);

        var window = new MainWindow
        {
            DataContext = new MainViewModel(engine, explainService, schemaTree, schemaService, completionProvider),
        };

        if (tunnel is not null)
        {
            window.Closed += (_, _) => tunnel.Dispose();
        }

        _ = schemaTree.RefreshCommand.ExecuteAsync(null);
        _ = completionProvider.RefreshAsync(CancellationToken.None);

        return window;
    }
}
