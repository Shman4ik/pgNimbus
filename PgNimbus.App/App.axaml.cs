using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App;

public partial class App : Application
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres;Application Name=pgNimbus";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // TODO: replace with a real connection manager UI; for now the
            // app connects using whatever PGNIMBUS_CONN provides.
            var connectionString = Environment.GetEnvironmentVariable("PGNIMBUS_CONN") ?? DefaultConnectionString;
            var dataSource = NpgsqlDataSource.Create(connectionString);
            var engine = new QueryEngine(dataSource);
            var schemaService = new SchemaService(dataSource);
            var schemaTree = new SchemaTreeViewModel(schemaService);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(new QueryViewModel(engine), schemaTree),
            };

            _ = schemaTree.RefreshCommand.ExecuteAsync(null);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
