using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Backup;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The backup window (issue #381) on the headless platform, over the fixture's
/// fake service: no server and no pg_dump, so these hold what the window does
/// with what the service and the search answer. The real pg_dump runs in
/// Core's <c>BackupServiceLiveTests</c>.
/// </summary>
[NotInParallel]
public class BackupWindowTests
{
    private static BackupViewModel Model(FakeBackupService service, PgToolStatusViewModel? tools = null, BackupScope? scope = null)
    {
        var model = new BackupViewModel(
            service,
            scope ?? BackupScope.Database,
            "prod-eu · app@db.example.com:5432/shop",
            lastFolder: null,
            tools: tools ?? new PgToolStatusViewModel(PgTool.PgDump, scan: _ => Task.FromResult(Fixtures.PgTools())),
            now: () => new DateTime(2026, 10, 10, 14, 32, 0));
        model.OutputPath = Path.Combine(Path.GetTempPath(), "shop_2026-10-10_1432.dump");
        return model;
    }

    /// <summary>
    /// Shows the window and waits for its search to land. Waited for by time, not
    /// by dispatcher passes: the server version is read on the thread pool, and in
    /// a full run, beside the completion replay, 200 passes went by before it came
    /// back and Back Up was still disabled.
    /// </summary>
    private static async Task<BackupWindow> OpenAsync(BackupViewModel model)
    {
        var window = new BackupWindow { DataContext = model };
        Ui.Show(window);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (model.Tools.State == PgToolState.Searching && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            Ui.Settle(passes: 1);
        }

        return window;
    }

    [Test]
    public async Task The_form_offers_back_up_once_a_pg_dump_new_enough_is_found()
    {
        await Ui.Run(async () =>
        {
            var model = Model(new FakeBackupService());
            var window = await OpenAsync(model);

            await Assert.That(model.Title).IsEqualTo("Back up shop");
            await Assert.That(window.FindControl<Border>("FormCard")!.IsVisible).IsTrue();
            await Assert.That(window.FindControl<Border>("ToolsMissingCard")!.IsVisible).IsFalse();
            await Assert.That(model.StartCommand.CanExecute(null)).IsTrue();
            await Assert.That(model.Tools.StatusLine).IsEqualTo(@"pg_dump 18.6 · C:\Program Files\PostgreSQL\18\bin");

            window.Close();
        });
    }

    [Test]
    public async Task Back_Up_runs_the_plan_on_the_form_and_says_where_the_file_went()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeBackupService();
            var model = Model(service, scope: BackupScope.ForSchema("sales"));
            var window = await OpenAsync(model);

            window.FindControl<RadioButton>("StructureOnlyChoice")!.IsChecked = true;
            await model.StartCommand.ExecuteAsync(null);
            Ui.Settle();

            var plan = service.Runs.Single();
            await Assert.That(plan.Scope).IsEqualTo(BackupScope.ForSchema("sales"));
            await Assert.That(plan.Content).IsEqualTo(BackupContent.StructureOnly);
            await Assert.That(plan.OutputPath).IsEqualTo(model.OutputPath);
            await Assert.That(model.State).IsEqualTo(BackupWindowState.Succeeded);
            await Assert.That(model.ResultTitle).IsEqualTo($"Saved {Core.ByteSize.Format(50_593_792)} in 0:42");
            await Assert.That(window.FindControl<Button>("ShowInFolderButton")!.IsEffectivelyVisible).IsTrue();

            window.Close();
        });
    }

    [Test]
    public async Task The_format_follows_the_file_name()
    {
        await Ui.Run(async () =>
        {
            var model = Model(new FakeBackupService());
            await Assert.That(model.Plan.Format).IsEqualTo(BackupFormat.Archive);
            await Assert.That(model.FormatHint).Contains("pg_restore");

            model.OutputPath = Path.ChangeExtension(model.OutputPath, ".sql");

            await Assert.That(model.Plan.Format).IsEqualTo(BackupFormat.SqlScript);
            await Assert.That(model.FormatHint).Contains("psql");
        });
    }

    [Test]
    public async Task The_command_preview_never_shows_the_password()
    {
        await Ui.Run(async () =>
        {
            var model = Model(new FakeBackupService());
            var window = await OpenAsync(model);

            await Assert.That(model.CommandPreview).Contains("--format=custom");
            await Assert.That(model.CommandPreview).Contains("host=db.example.com");
            await Assert.That(model.CommandPreview).Contains("--file=" + model.OutputPath);
            await Assert.That(model.CommandPreview).DoesNotContain(".partial");
            await Assert.That(model.CommandPreview).DoesNotContain(FakeBackupService.Connection.Password!);

            window.Close();
        });
    }

    [Test]
    public async Task With_no_pg_dump_new_enough_the_window_names_the_version_and_the_steps()
    {
        await Ui.Run(async () =>
        {
            var scan = new PgToolScan(
                [new PgToolInstall("/usr/lib/postgresql/15/bin", new PgVersion(15, 3), null, [])],
                [new PgToolProblem("/opt/broken", "it doesn't start: a library it needs is missing")],
                null);
            var tools = new PgToolStatusViewModel(
                PgTool.PgDump,
                scan: _ => Task.FromResult(scan),
                guide: server => PgToolInstallGuide.Steps(PgToolPlatform.Linux, PgToolInstallGuide.MajorToInstall(server), LinuxFamily.Ubuntu));
            var model = Model(new FakeBackupService(), tools);
            var window = await OpenAsync(model);

            await Assert.That(window.FindControl<Border>("ToolsMissingCard")!.IsVisible).IsTrue();
            await Assert.That(window.FindControl<Border>("FormCard")!.IsVisible).IsFalse();
            await Assert.That(window.FindControl<Button>("StartButton")!.IsVisible).IsFalse();
            await Assert.That(model.StartCommand.CanExecute(null)).IsFalse();

            await Assert.That(tools.Headline).IsEqualTo("pgNimbus needs pg_dump 17 or newer");
            await Assert.That(tools.Explanation).Contains("The newest pg_dump on this computer is 15.3, in /usr/lib/postgresql/15/bin.");
            await Assert.That(tools.Explanation).Contains("This server runs PostgreSQL 17.4");
            await Assert.That(tools.Explanation).Contains("There's a pg_dump in /opt/broken, but it doesn't start");
            await Assert.That(tools.Steps.Select(s => s.Command).OfType<string>()).Contains("sudo apt install postgresql-client-18");

            // Every step is on screen, each command with its own Copy button.
            // The guide is its own control (and name scope), so it is found by walking.
            var steps = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "GuideSteps");
            await Assert.That(steps.GetVisualDescendants().OfType<Button>().Count(b => (b.Content as string) == "Copy" && b.IsEffectivelyVisible))
                .IsEqualTo(tools.Steps.Count(s => s.HasCommand));

            window.Close();
        });
    }

    [Test]
    public async Task Look_Again_picks_up_a_pg_dump_installed_since()
    {
        await Ui.Run(async () =>
        {
            var installed = false;
            var tools = new PgToolStatusViewModel(
                PgTool.PgDump,
                scan: _ => Task.FromResult(installed ? Fixtures.PgTools() : PgToolScan.Empty),
                guide: _ => []);
            var model = Model(new FakeBackupService(), tools);
            var window = await OpenAsync(model);
            await Assert.That(model.ShowToolsMissing).IsTrue();

            installed = true;
            await tools.LookAgainCommand.ExecuteAsync(null);
            Ui.Settle();

            await Assert.That(model.ShowForm).IsTrue();
            await Assert.That(model.StartCommand.CanExecute(null)).IsTrue();

            window.Close();
        });
    }

    [Test]
    public async Task A_failed_backup_shows_pg_dumps_error_and_the_hint_and_Try_Again_keeps_the_choices()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeBackupService
            {
                Run = (_, plan, _, _) => Task.FromResult(new BackupResult(
                    BackupOutcome.Failed, plan.OutputPath, 0, TimeSpan.FromSeconds(1), 0,
                    "connection to server failed: FATAL:  password authentication failed for user \"app\"",
                    "The server refused the password.",
                    "pg_dump: error: connection to server failed")),
            };
            var model = Model(service);
            var window = await OpenAsync(model);
            model.StructureOnly = true;

            await model.StartCommand.ExecuteAsync(null);
            Ui.Settle();

            await Assert.That(model.State).IsEqualTo(BackupWindowState.Failed);
            await Assert.That(model.ResultDetail).Contains("password authentication failed");
            await Assert.That(model.ResultHint).IsEqualTo("The server refused the password.");
            await Assert.That(window.FindControl<Button>("TryAgainButton")!.IsEffectivelyVisible).IsTrue();

            model.TryAgainCommand.Execute(null);
            await Assert.That(model.State).IsEqualTo(BackupWindowState.Setup);
            await Assert.That(model.StructureOnly).IsTrue();

            window.Close();
        });
    }

    [Test]
    public async Task Closing_a_window_whose_backup_runs_asks_first_and_stops_it()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeBackupService
            {
                Run = async (_, plan, _, token) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return new BackupResult(BackupOutcome.Cancelled, plan.OutputPath, 0, TimeSpan.Zero, 0, null, null, "");
                },
            };
            var model = Model(service);
            var window = await OpenAsync(model);

            var running = model.StartCommand.ExecuteAsync(null);
            Ui.Settle();
            await Assert.That(model.IsRunning).IsTrue();

            // Declining keeps the backup and the window.
            window.Close();
            Ui.Settle();
            window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(false);
            Ui.Settle();
            await Assert.That(window.IsVisible).IsTrue();
            await Assert.That(model.IsRunning).IsTrue();

            // Accepting stops pg_dump, then closes.
            window.Close();
            Ui.Settle();
            window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(true);
            await Assert.That(Ui.SettleUntil(() => !window.IsVisible)).IsTrue();
            await running;
            await Assert.That(model.State).IsEqualTo(BackupWindowState.Cancelled);
        });
    }

    [Test]
    public async Task The_palette_command_and_the_tree_menus_open_the_backup_window_on_their_object()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.BackupDatabaseCommand.Execute(null);
            Ui.Settle();
            var backup = window.OwnedWindows.OfType<BackupWindow>().Single();
            await Assert.That(((BackupViewModel)backup.DataContext!).Scope).IsEqualTo(BackupScope.Database);

            // An idle window is pointed at the next thing asked for, not doubled.
            var orders = vm.SchemaTree.Schemas.SelectMany(s => s.Children).OfType<TableNode>().First(t => t.Name == "orders");
            vm.SchemaTree.BackUpTableRequested!(orders);
            Ui.Settle();
            await Assert.That(window.OwnedWindows.OfType<BackupWindow>().Count()).IsEqualTo(1);
            await Assert.That(((BackupViewModel)backup.DataContext!).Scope).IsEqualTo(BackupScope.ForTable("public", "orders", isPartitioned: false));
            await Assert.That(((BackupViewModel)backup.DataContext!).Title).IsEqualTo("Back up table public.orders");

            var schema = vm.SchemaTree.Schemas.OfType<SchemaNode>().First(s => s.Name == "public");
            vm.SchemaTree.BackUpSchemaRequested!(schema);
            Ui.Settle();
            await Assert.That(((BackupViewModel)backup.DataContext!).Scope).IsEqualTo(BackupScope.ForSchema("public"));

            backup.Close();
            window.Close();
        });
    }

    [Test]
    public async Task A_window_without_a_pg_dump_connection_says_so_instead_of_opening()
    {
        await Ui.Run(async () =>
        {
            var vm = new MainViewModel(
                new Core.Query.QueryEngine(Fixtures.DataSource),
                new Core.Query.ExplainService(Fixtures.DataSource),
                new SchemaTreeViewModel(new Core.Schema.SchemaService(Fixtures.DataSource)),
                new Core.Schema.SchemaService(Fixtures.DataSource),
                new Core.Schema.SchemaEditor(Fixtures.DataSource),
                new Core.Schema.DdlService(Fixtures.DataSource),
                new PgNimbus.App.Completion.SqlCompletionProvider(new Core.Schema.SchemaService(Fixtures.DataSource)),
                new NotifyMonitorViewModel(new Core.Notifications.NotificationListener(Fixtures.DataSource)),
                new Core.Monitoring.ActivityService(Fixtures.DataSource),
                new Core.Monitoring.DatabaseStatsService(Fixtures.DataSource),
                new Core.Monitoring.StatementStatsService(Fixtures.DataSource),
                new Core.Security.RoleService(Fixtures.DataSource),
                new Core.Security.PrivilegeService(Fixtures.DataSource),
                new Core.Security.SecurityEditor(Fixtures.DataSource),
                new Core.Import.ImportService(Fixtures.DataSource),
                savedQueryStore: new Core.Query.SavedQueryStore(Path.Combine(IsolatedAppData.NewDirectory("backup"), "saved.json")),
                historyStore: new Core.Query.QueryHistoryStore(Path.Combine(IsolatedAppData.NewDirectory("backup"), "history.json")));
            var requested = false;
            vm.BackupRequested += _ => requested = true;

            vm.BackupDatabaseCommand.Execute(null);

            await Assert.That(requested).IsFalse();
            await Assert.That(vm.ActiveTab.Status).Contains("pg_dump");
        });
    }
}
