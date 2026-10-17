using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Environments on a profile (ROADMAP T3): the picker in the connection form,
/// the label in the title bar, and production's switch, which opens every
/// window with writes off and allows them for one window after a confirm. The
/// server's refusal itself is covered in Core (ProductionWriteGateTests).
/// </summary>
[NotInParallel]
public class ProductionEnvironmentTests
{
    private static Func<CancellationToken, Task<SessionWriteState>> Server(SessionWriteState state) =>
        _ => Task.FromResult(state);

    private static (Window Window, MainViewModel ViewModel) Shell(
        ConnectionEnvironment environment, bool readOnlyProfile = false, bool confirm = true)
    {
        var vm = Fixtures.MainWindowViewModel(readOnlyProfile: readOnlyProfile, environment: environment);
        var window = new MainWindow { DataContext = vm };
        Ui.Show(window);

        // After the window took the view model: it wires the real confirm dialog.
        vm.ConfirmAllowWritesRequested = () => Task.FromResult(confirm);
        vm.WriteStateProbe = _ => Task.FromResult(
            vm.WritesAllowed ? SessionWriteState.ReadWrite : SessionWriteState.ReadOnly);
        return (window, vm);
    }

    private static T Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;

    [Test]
    public async Task A_production_window_opens_with_writes_off()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production);
            await vm.DetectWriteStateAsync();
            Ui.Settle();

            await Assert.That(vm.CanToggleWrites).IsTrue();
            await Assert.That(vm.WritesAllowed).IsFalse();
            await Assert.That(vm.ConnectionReadOnlyHint).IsEqualTo(MainViewModel.ProductionLockedHint);

            // The switch says it, so the read-only mark doesn't say it twice.
            var button = Find<Button>(window, "ProductionWritesButton");
            await Assert.That(button.IsEffectivelyVisible).IsTrue();
            await Assert.That(button.Classes.Contains("writesOn")).IsFalse();
            await Assert.That(Find<StackPanel>(window, "ReadOnlyMark").IsEffectivelyVisible).IsFalse();
            await Assert.That(Find<Border>(window, "EnvironmentLabel").IsEffectivelyVisible).IsFalse();

            // A tab offers no editing, and Server Activity doesn't signal.
            var tab = vm.ActiveTab;
            tab.EditContext = new EditableTableContext("public", "orders", ["id"], []);
            tab.ApplyConnectionReadOnly();
            await Assert.That(tab.EditContext).IsNull();
            await Assert.That(vm.Activity.SignalsLockedHint).IsNotNull();
            await Assert.That(vm.Activity.CanSignal).IsFalse();
            await Assert.That(vm.Activity.TerminateTip).IsEqualTo(vm.Activity.SignalsLockedHint!);
            window.Close();
        });
    }

    [Test]
    public async Task Declining_the_confirm_changes_nothing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production, confirm: false);

            await vm.AllowWritesCommand.ExecuteAsync(null);

            await Assert.That(vm.WritesAllowed).IsFalse();
            await Assert.That(vm.ConnectionReadOnlyHint).IsEqualTo(MainViewModel.ProductionLockedHint);
            await Assert.That(vm.Activity.SignalsLockedHint).IsNotNull();
            window.Close();
        });
    }

    [Test]
    public async Task No_confirm_handler_means_no_writes()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production);
            vm.ConfirmAllowWritesRequested = null;

            await vm.AllowWritesCommand.ExecuteAsync(null);

            await Assert.That(vm.WritesAllowed).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task Clicking_the_switch_allows_writes_and_clicking_again_turns_them_off()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production);
            var tab = vm.ActiveTab;
            tab.ReadOnlyHint = vm.ConnectionReadOnlyHint;
            var button = Find<Button>(window, "ProductionWritesButton");

            Click(window, button);
            await Assert.That(Ui.SettleUntil(() => vm.WritesAllowed)).IsTrue();
            Ui.Settle();

            await Assert.That(vm.ConnectionReadOnlyHint).IsNull();
            await Assert.That(button.Classes.Contains("writesOn")).IsTrue();
            await Assert.That(Find<StackPanel>(window, "ReadOnlyMark").IsEffectivelyVisible).IsFalse();
            await Assert.That(vm.Activity.SignalsLockedHint).IsNull();
            await Assert.That(vm.ActiveTab.Status).Contains("Writes are allowed in this window");

            // The rows on screen were loaded read-only; the tab says how to edit them.
            await Assert.That(tab.ReadOnlyHint).IsEqualTo(QueryViewModel.WritesAllowedRerunHint);

            Click(window, button);
            await Assert.That(Ui.SettleUntil(() => !vm.WritesAllowed)).IsTrue();
            Ui.Settle();

            await Assert.That(vm.ConnectionReadOnlyHint).IsEqualTo(MainViewModel.ProductionLockedHint);
            await Assert.That(button.Classes.Contains("writesOn")).IsFalse();
            await Assert.That(vm.Activity.SignalsLockedHint).IsNotNull();
            window.Close();
        });
    }

    [Test]
    public async Task Writes_stay_on_while_a_transaction_holds_a_writable_connection()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production);
            await vm.AllowWritesCommand.ExecuteAsync(null);
            vm.IsInTransaction = true;

            await vm.LockWritesCommand.ExecuteAsync(null);

            await Assert.That(vm.WritesAllowed).IsTrue();
            await Assert.That(vm.ActiveTab.HasError).IsTrue();
            await Assert.That(vm.ActiveTab.Status).Contains("Commit or roll back");

            vm.IsInTransaction = false;
            await vm.LockWritesCommand.ExecuteAsync(null);
            await Assert.That(vm.WritesAllowed).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task A_role_that_cannot_write_stays_read_only_after_writes_are_allowed()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production);
            vm.WriteStateProbe = Server(SessionWriteState.ReadOnly);

            await vm.AllowWritesCommand.ExecuteAsync(null);
            Ui.Settle();

            // The pool can write; the server (a role default) still says no, and
            // the mark comes back with the server's reason.
            await Assert.That(vm.WritesAllowed).IsTrue();
            await Assert.That(vm.ConnectionReadOnlyHint).IsEqualTo("the connection is read-only, so the server refuses writes.");
            await Assert.That(Find<StackPanel>(window, "ReadOnlyMark").IsEffectivelyVisible).IsTrue();
            window.Close();
        });
    }

    [Test]
    public async Task A_pooler_that_drops_the_option_turns_the_mark_amber_in_production()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production);

            await vm.DetectWriteStateAsync(Server(SessionWriteState.ReadWrite));
            Ui.Settle();

            await Assert.That(vm.IsReadOnlyNotEnforced).IsTrue();
            await Assert.That(vm.ConnectionReadOnlyHint!).Contains("production");
            var mark = Find<StackPanel>(window, "ReadOnlyMark");
            await Assert.That(mark.IsEffectivelyVisible).IsTrue();
            await Assert.That(mark.Classes.Contains("warn")).IsTrue();
            window.Close();
        });
    }

    [Test]
    public async Task A_read_only_production_profile_shows_a_label_and_cannot_allow_writes()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Shell(ConnectionEnvironment.Production, readOnlyProfile: true);
            Ui.Settle();

            await Assert.That(vm.CanToggleWrites).IsFalse();
            await Assert.That(Find<Button>(window, "ProductionWritesButton").IsEffectivelyVisible).IsFalse();
            var label = Find<Border>(window, "EnvironmentLabel");
            await Assert.That(label.IsEffectivelyVisible).IsTrue();
            await Assert.That(label.Classes.Contains("production")).IsTrue();
            await Assert.That(Find<StackPanel>(window, "ReadOnlyMark").IsEffectivelyVisible).IsTrue();

            await vm.AllowWritesCommand.ExecuteAsync(null);
            await Assert.That(vm.WritesAllowed).IsFalse();
            await Assert.That(vm.ActiveTab.Status).Contains("read-only");

            // Not a production switch, so Server Activity isn't held back by it.
            await Assert.That(vm.Activity.SignalsLockedHint).IsNull();
            window.Close();
        });
    }

    [Test]
    public async Task Staging_and_dev_are_labels_and_change_nothing_else()
    {
        await Ui.Run(async () =>
        {
            foreach (var (environment, tag) in new[] { (ConnectionEnvironment.Staging, "staging"), (ConnectionEnvironment.Development, "dev") })
            {
                var (window, vm) = Shell(environment);
                Ui.Settle();

                var label = Find<Border>(window, "EnvironmentLabel");
                await Assert.That(label.IsEffectivelyVisible).IsTrue();
                await Assert.That(vm.EnvironmentTag).IsEqualTo(tag);
                await Assert.That(label.Classes.Contains("staging")).IsEqualTo(environment == ConnectionEnvironment.Staging);
                await Assert.That(Find<Button>(window, "ProductionWritesButton").IsEffectivelyVisible).IsFalse();
                await Assert.That(vm.IsReadOnlyConnection).IsFalse();
                await Assert.That(vm.Activity.SignalsLockedHint).IsNull();
                window.Close();
            }

            // And no label at all without an environment.
            var (plain, _) = Shell(ConnectionEnvironment.None);
            Ui.Settle();
            await Assert.That(Find<Border>(plain, "EnvironmentLabel").IsEffectivelyVisible).IsFalse();
            plain.Close();
        });
    }

    [Test]
    public async Task The_environment_is_saved_with_the_profile()
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
                vm.Environment = ConnectionEnvironment.Production;

                await Assert.That(new ConnectionProfileStore(path).Load().Single().Environment).IsEqualTo(ConnectionEnvironment.Production);
                await Assert.That(vm.IsProductionEnvironment).IsTrue();
                await Assert.That(vm.EnvironmentHint).Contains("Allow them");

                // Read-only too: the line no longer promises that writes can be allowed.
                vm.ReadOnly = true;
                await Assert.That(vm.EnvironmentHint).Contains("stay off in every window");

                var reopened = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore(), vm.SelectedProfile!.Id);
                await Assert.That(reopened.Environment).IsEqualTo(ConnectionEnvironment.Production);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });
    }

    [Test]
    public async Task The_palette_lists_the_one_switch_that_applies_and_only_in_production()
    {
        await Ui.Run(async () =>
        {
            const string Allow = "Allow writes in this window";
            const string Lock = "Turn writes off in this window";

            var (plain, plainVm) = Shell(ConnectionEnvironment.Staging);
            var titles = PaletteTitles(plainVm, "writes");
            await Assert.That(titles).DoesNotContain(Allow);
            await Assert.That(titles).DoesNotContain(Lock);
            plain.Close();

            var (window, vm) = Shell(ConnectionEnvironment.Production);
            titles = PaletteTitles(vm, "writes");
            await Assert.That(titles).Contains(Allow);
            await Assert.That(titles).DoesNotContain(Lock);

            await vm.AllowWritesCommand.ExecuteAsync(null);
            titles = PaletteTitles(vm, "writes");
            await Assert.That(titles).DoesNotContain(Allow);
            await Assert.That(titles).Contains(Lock);
            window.Close();
        });
    }

    private static List<string> PaletteTitles(MainViewModel vm, string search)
    {
        // Fire and forget: the table half of the palette waits on a catalog
        // fetch that never answers against the fixture data source.
        _ = vm.OpenCommandPaletteAsync();
        Ui.Settle();
        vm.CommandPalette.SearchText = search;
        Ui.Settle();
        var titles = vm.CommandPalette.Results.Select(i => i.Title).ToList();
        vm.CommandPalette.CloseCommand.Execute(null);
        Ui.Settle();
        return titles;
    }

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }
}
