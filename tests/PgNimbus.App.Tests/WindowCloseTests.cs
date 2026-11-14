using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Backup;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Closing a main window ends its connection, so a close that would lose
/// something (staged safe-mode changes, the open transaction, a running query)
/// asks first through the shared confirm, naming it, and a close that would lose
/// nothing asks nothing. The server-side half (the transaction rolled back, the
/// query cancelled) is <see cref="WindowCloseLiveTests"/>.
/// </summary>
public class WindowCloseTests
{
    [Test]
    public async Task Each_loss_is_asked_about_by_name_and_cancel_keeps_the_window_and_its_state()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Open();

            // (a) staged changes.
            vm.ActiveTab.PendingChanges = Staged();
            await AskedAndDeclined(window, "Close this window? 3 staged changes will be discarded.");
            await Assert.That(vm.ActiveTab.PendingChanges!.Count).IsEqualTo(3);
            vm.ActiveTab.PendingChanges = null;

            // (b) the open transaction.
            vm.IsInTransaction = true;
            await AskedAndDeclined(window, "Close this window? The open transaction will be rolled back.");
            await Assert.That(vm.IsInTransaction).IsTrue();
            vm.IsInTransaction = false;

            // (c) a running query.
            vm.ActiveTab.IsRunning = true;
            await AskedAndDeclined(window, "Close this window? The running query will be cancelled.");
            await Assert.That(vm.ActiveTab.IsRunning).IsTrue();

            // All three in one sentence, and Close goes ahead.
            vm.ActiveTab.PendingChanges = Staged();
            vm.IsInTransaction = true;
            window.Close();
            Ui.Settle();
            var dialog = Dialog(window);
            await Assert.That(Text(dialog)).Contains(
                "Close this window? 3 staged changes will be discarded, the open transaction will be rolled back and the running query will be cancelled.");
            await Assert.That((string?)dialog.FindControl<Button>("ConfirmButton")!.Content).IsEqualTo("Close Window");
            dialog.Close(true);
            await Assert.That(await Ui.WaitUntilAsync(() => !window.IsVisible)).IsTrue();
        });
    }

    [Test]
    public async Task A_window_with_nothing_to_lose_closes_without_a_question()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Open();
            vm.ActiveTab.Sql = "SELECT 'typed, never saved';";

            window.Close();
            Ui.Settle();
            await Assert.That(window.OwnedWindows.OfType<ConfirmDialog>().Any()).IsFalse();
            await Assert.That(window.IsVisible).IsFalse();
        });
    }

    [Test]
    public async Task A_second_close_while_the_question_is_open_asks_nothing_more()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Open();
            vm.IsInTransaction = true;

            window.Close();
            Ui.Settle();
            window.Close();
            Ui.Settle();
            await Assert.That(window.OwnedWindows.OfType<ConfirmDialog>().Count()).IsEqualTo(1);
            await Assert.That(window.IsVisible).IsTrue();

            Dialog(window).Close(true);
            await Assert.That(await Ui.WaitUntilAsync(() => !window.IsVisible)).IsTrue();
        });
    }

    [Test]
    public async Task The_database_switch_asks_once_and_its_close_asks_nothing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Open();
            var asked = new List<string>();
            vm.ConfirmConnectionLossRequested = (message, _) =>
            {
                asked.Add(message);
                return Task.FromResult(true);
            };

            // What App.SwitchDatabaseInWindowAsync does once the new window is up.
            vm.SwitchToDatabase = _ =>
            {
                ((MainWindow)window).CloseConfirmed();
                return Task.CompletedTask;
            };
            vm.ActiveTab.PendingChanges = Staged();

            await vm.SwitchToDatabaseAsync("analytics");
            Ui.Settle();
            await Assert.That(asked).IsEquivalentTo(new[] { "Open analytics in this window? 3 staged changes will be discarded." });
            await Assert.That(window.OwnedWindows.OfType<ConfirmDialog>().Any()).IsFalse();
            await Assert.That(window.IsVisible).IsFalse();
        });
    }

    [Test]
    public async Task A_running_backup_and_the_connections_losses_are_one_question()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Open();
            var service = (FakeBackupService)vm.Backups!;
            service.Run = async (_, plan, _, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                }

                return new BackupResult(BackupOutcome.Cancelled, plan.OutputPath, 0, TimeSpan.Zero, 0, null, null, "");
            };

            vm.BackupDatabaseCommand.Execute(null);
            Ui.Settle();
            var backupWindow = window.OwnedWindows.OfType<BackupWindow>().Single();
            var backup = (BackupViewModel)backupWindow.DataContext!;
            await Assert.That(await Ui.WaitUntilAsync(() => backup.StartCommand.CanExecute(null))).IsTrue();
            var running = backup.StartCommand.ExecuteAsync(null);
            Ui.Settle();
            await Assert.That(backup.IsRunning).IsTrue();

            // The backup alone still asks the way it always did.
            window.Close();
            Ui.Settle();
            var dialog = Dialog(window);
            await Assert.That(Text(dialog)).Contains(
                "A backup is still running, and closing this window ends its connection. Stop the backup and close?");
            await Assert.That((string?)dialog.FindControl<Button>("ConfirmButton")!.Content).IsEqualTo("Stop Backup");
            dialog.Close(false);
            Ui.Settle();
            await Assert.That(window.IsVisible).IsTrue();
            await Assert.That(backup.IsRunning).IsTrue();

            // With staged changes as well, it is still one question.
            vm.ActiveTab.PendingChanges = Staged();
            window.Close();
            Ui.Settle();
            dialog = Dialog(window);
            await Assert.That(Text(dialog)).Contains(
                "A backup is still running, and closing this window ends its connection. Stop the backup and close? 3 staged changes will be discarded.");
            dialog.Close(true);
            await Assert.That(await Ui.WaitUntilAsync(() => !window.IsVisible)).IsTrue();
            await running;
            await Assert.That(backup.State).IsEqualTo(BackupWindowState.Cancelled);
            await Assert.That(window.OwnedWindows.OfType<ConfirmDialog>().Any()).IsFalse();
        });
    }

    [Test]
    public async Task The_question_puts_a_running_tool_first_and_is_null_when_nothing_is_lost()
    {
        await Assert.That(MainWindow.CloseQuestion(false, false, null)).IsNull();
        await Assert.That(MainWindow.CloseQuestion(false, false, "The open transaction will be rolled back."))
            .IsEqualTo(("Close this window? The open transaction will be rolled back.", "Close Window"));
        await Assert.That(MainWindow.CloseQuestion(true, true, null)!.Value.Label).IsEqualTo("Stop Restore");
        await Assert.That(MainWindow.CloseQuestion(false, true, "The running query will be cancelled.")!.Value.Message)
            .EndsWith("nothing it did stays. The running query will be cancelled.");
    }

    private static (Window Window, MainViewModel ViewModel) Open()
    {
        var vm = Fixtures.MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        Ui.Show(window);
        return (window, vm);
    }

    private static PendingChangeSet Staged()
    {
        var staged = new PendingChangeSet("public", "orders", ["id"]);
        staged.StageEdit([1L], "status", "paid");
        staged.StageEdit([2L], "status", "paid");
        staged.StageDelete([3L]);
        return staged;
    }

    private static async Task AskedAndDeclined(Window window, string message)
    {
        window.Close();
        Ui.Settle();
        var dialog = Dialog(window);
        await Assert.That(Text(dialog)).Contains(message);
        await Assert.That(dialog.FindControl<Button>("CancelButton")!.IsCancel).IsTrue();
        await Assert.That(dialog.FindControl<Button>("ConfirmButton")!.Classes.Contains("danger")).IsTrue();
        await Assert.That(dialog.FindControl<Button>("ConfirmButton")!.IsDefault).IsFalse();
        dialog.Close(false);
        Ui.Settle();
        await Assert.That(window.IsVisible).IsTrue();
        await Assert.That(window.OwnedWindows.OfType<ConfirmDialog>().Any()).IsFalse();
    }

    private static ConfirmDialog Dialog(Window window) => window.OwnedWindows.OfType<ConfirmDialog>().Single();

    private static List<string?> Text(Window dialog) =>
        dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
}
