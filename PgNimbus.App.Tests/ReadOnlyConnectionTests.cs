using Avalonia.Controls;
using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A read-only connection (ROADMAP T3) has three visible parts: the switch in
/// the connection form, the mark beside host › database, and a grid that offers
/// no editing. The server's refusal itself is covered in Core.
/// </summary>
[NotInParallel]
public class ReadOnlyConnectionTests
{
    [Test]
    public async Task The_read_only_switch_is_saved_with_the_profile()
    {
        await Ui.Run(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-dialog-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "profiles.json");
            try
            {
                var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore())
                {
                    Host = "prod.example.com",
                    Database = "app",
                };
                vm.ReadOnly = true;

                await Assert.That(new ConnectionProfileStore(path).Load().Single().ReadOnly).IsTrue();

                // And reopening the form on that profile shows it switched on.
                var reopened = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore(), vm.SelectedProfile!.Id);
                await Assert.That(reopened.ReadOnly).IsTrue();
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });
    }

    [Test]
    public async Task The_title_bar_marks_a_connection_that_cannot_write()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var mark = window.FindControl<StackPanel>("ReadOnlyMark")!;
            await Assert.That(mark.IsEffectivelyVisible).IsFalse();

            vm.ConnectionReadOnlyHint = "the server is a standby replica, which refuses writes.";
            Ui.Settle();
            await Assert.That(mark.IsEffectivelyVisible).IsTrue();
        });
    }

    // --- The profile's wish against the server's answer (audit finding 17) ---

    private static Func<CancellationToken, Task<SessionWriteState>> Server(SessionWriteState state) =>
        _ => Task.FromResult(state);

    private static (Window Window, MainViewModel ViewModel) ShellFor(bool readOnlyProfile)
    {
        var vm = Fixtures.MainWindowViewModel(readOnlyProfile);
        var window = new MainWindow { DataContext = vm };
        return (window, vm);
    }

    [Test]
    public async Task A_writable_profile_on_a_writable_server_shows_no_mark()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = ShellFor(readOnlyProfile: false);
            Ui.Show(window);

            await vm.DetectWriteStateAsync(Server(SessionWriteState.ReadWrite));
            Ui.Settle();

            await Assert.That(vm.ConnectionReadOnlyHint).IsNull();
            await Assert.That(vm.IsReadOnlyNotEnforced).IsFalse();
            await Assert.That(window.FindControl<StackPanel>("ReadOnlyMark")!.IsEffectivelyVisible).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task A_read_only_profile_the_server_honours_shows_the_plain_mark()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = ShellFor(readOnlyProfile: true);
            Ui.Show(window);

            await vm.DetectWriteStateAsync(Server(SessionWriteState.ReadOnly));
            Ui.Settle();

            var mark = window.FindControl<StackPanel>("ReadOnlyMark")!;
            await Assert.That(vm.ConnectionReadOnlyHint).IsEqualTo(Hint);
            await Assert.That(vm.IsReadOnlyNotEnforced).IsFalse();
            await Assert.That(mark.IsEffectivelyVisible).IsTrue();
            await Assert.That(mark.Classes.Contains("warn")).IsFalse();
            await Assert.That(window.FindControl<TextBlock>("ReadOnlyNotEnforcedText")!.IsEffectivelyVisible).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task A_read_only_profile_the_server_ignores_keeps_the_grid_read_only_and_warns()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = ShellFor(readOnlyProfile: true);
            Ui.Show(window);

            // A result already on screen, read-only for the profile's reason,
            // and a second one that somehow holds an edit context.
            var tab = vm.ActiveTab;
            tab.ReadOnlyHint = vm.ConnectionReadOnlyHint;
            vm.AddTabCommand.Execute(null);
            var other = vm.ActiveTab;
            other.EditContext = new EditableTableContext("public", "orders", ["id"], []);

            // PgBouncer with ignore_startup_parameters = options: the option
            // never reached the server, which reports a writable session.
            await vm.DetectWriteStateAsync(Server(SessionWriteState.ReadWrite));
            Ui.Settle();

            // The hint is kept (reworded), so every tab still refuses editing...
            await Assert.That(vm.IsReadOnlyNotEnforced).IsTrue();
            await Assert.That(vm.ConnectionReadOnlyHint).IsNotNull();
            await Assert.That(vm.ConnectionReadOnlyHint!).Contains("did not apply");
            await Assert.That(other.EditContext).IsNull();
            await Assert.That(other.ReadOnlyHint).IsEqualTo(vm.ConnectionReadOnlyHint);

            // ...and a result that was read-only for the old reason no longer
            // claims the server refuses writes.
            await Assert.That(tab.ReadOnlyHint).IsEqualTo(vm.ConnectionReadOnlyHint);

            // The mark is amber and says what happened; the status line says it once.
            var mark = window.FindControl<StackPanel>("ReadOnlyMark")!;
            await Assert.That(mark.IsEffectivelyVisible).IsTrue();
            await Assert.That(mark.Classes.Contains("warn")).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("ReadOnlyNotEnforcedText")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(ToolTip.GetTip(mark) as string).Contains("SQL you run can still change data");
            await Assert.That(vm.ActiveTab.Status).Contains("Read-only was not applied");
            window.Close();
        });
    }

    // --- Against a real server ----------------------------------------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private const string Table = "pgnimbus_readonly_grid_scratch";

    private const string Hint = "the connection is read-only, so the server refuses writes.";

    [Test]
    public async Task A_read_only_connection_offers_no_grid_editing()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to browse.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using (var seed = dataSource.CreateCommand(
            $"DROP TABLE IF EXISTS {Table}; CREATE TABLE {Table} (id int PRIMARY KEY, name text); INSERT INTO {Table} VALUES (1, 'a')"))
        {
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            await Ui.Run(async () =>
            {
                var schema = new SchemaService(dataSource);
                var columns = await schema.GetColumnsAsync("public", Table, CancellationToken.None);
                string? hint = null;
                var tab = new QueryViewModel(new QueryEngine(dataSource), new ExplainService(dataSource),
                    schemaService: schema, connectionReadOnlyHint: () => hint);

                // Writable: browsing gives the grid an edit context, as always.
                await tab.StartBrowseAsync("public", Table, columns);
                await Assert.That(tab.EditContext).IsNotNull();

                // The server's answer arrives after the tab ran: editing goes away
                // from the result already on screen...
                hint = Hint;
                tab.ApplyConnectionReadOnly();
                await Assert.That(tab.EditContext).IsNull();
                await Assert.That(tab.ReadOnlyHint).IsEqualTo(Hint);

                // ...and stays away on the next page load and on a typed query.
                await tab.Browse!.LoadAsync();
                await Assert.That(tab.EditContext).IsNull();
                await Assert.That(tab.ReadOnlyHint).IsEqualTo(Hint);

                tab.Sql = $"SELECT id, name FROM public.{Table}";
                await tab.RunCommand.ExecuteAsync(null);
                await Assert.That(tab.EditContext).IsNull();
                await Assert.That(tab.ReadOnlyHint).IsEqualTo(Hint);
            });
        }
        finally
        {
            await using var drop = dataSource.CreateCommand($"DROP TABLE IF EXISTS {Table}");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
