using Avalonia.Controls;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Backup;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The restore window (issue #381) on the headless platform, over the fixture's
/// fake service. What it must never do is run against the window's own
/// database without asking, run a script, or hide what a failure left behind;
/// the real pg_restore runs in Core's <c>RestoreServiceLiveTests</c>.
/// </summary>
[NotInParallel]
public class RestoreWindowTests
{
    private const string Archive = "/backups/shop.dump";

    private static RestoreViewModel Model(FakeRestoreService service, Func<string, Task>? openDatabase = null) =>
        new(service, "local · postgres@localhost:5432/shop",
            new PgToolStatusViewModel(PgTool.PgRestore, scan: _ => Task.FromResult(Fixtures.PgTools())),
            openDatabase);

    /// <summary>Shows the window and waits for its search and its server read (by time, see BackupWindowTests).</summary>
    private static async Task<RestoreWindow> OpenAsync(RestoreViewModel model)
    {
        var window = new RestoreWindow { DataContext = model, PickFileOnOpen = false };
        Ui.Show(window);
        await Ui.WaitUntilAsync(() => model.Tools.State != PgToolState.Searching);
        return window;
    }

    private static async Task InspectAsync(RestoreViewModel model)
    {
        await model.InspectAsync(Archive);
        Ui.Settle();
    }

    [Test]
    public async Task An_archive_is_shown_for_what_it_is_and_goes_into_a_new_database_by_default()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeRestoreService { Missing = ["reporting"] };
            var model = Model(service);
            var window = await OpenAsync(model);
            await InspectAsync(model);

            await Assert.That(model.State).IsEqualTo(RestoreWindowState.Ready);
            await Assert.That(model.ArchiveSummary).IsEqualTo("Database shop · PostgreSQL 17.4 · saved 2026-10-10 09:32:12 by pg_dump 18.6");
            await Assert.That(model.ContentSummary).IsEqualTo("4 tables, with their rows");
            // "shop" exists on the server, so the suggestion moves aside.
            await Assert.That(model.NewDatabaseName).IsEqualTo("shop_restored");
            await Assert.That(model.IntoNewDatabase).IsTrue();
            // A role the backup names is missing, so owners are not kept by default.
            await Assert.That(model.KeepOwners).IsFalse();
            await Assert.That(model.OwnersNote).Contains("reporting");
            await Assert.That(window.FindControl<Border>("FormCard")!.IsVisible).IsTrue();

            await model.StartCommand.ExecuteAsync(null);
            Ui.Settle();

            var plan = service.Runs.Single();
            await Assert.That(plan.Target).IsEqualTo(RestoreTarget.NewDatabase);
            await Assert.That(plan.DatabaseName).IsEqualTo("shop_restored");
            await Assert.That(plan.KeepOwners).IsFalse();
            await Assert.That(plan.ArchivePath).IsEqualTo(Archive);
            await Assert.That(model.State).IsEqualTo(RestoreWindowState.Succeeded);
            await Assert.That(model.ResultTitle).StartsWith("Restored into shop_restored");

            window.Close();
        });
    }

    [Test]
    public async Task Replacing_the_current_database_asks_first_and_declining_runs_nothing()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeRestoreService();
            var model = Model(service);
            var window = await OpenAsync(model);
            await InspectAsync(model);
            var asked = new List<string>();
            var answer = false;
            model.ConfirmReplace = message =>
            {
                asked.Add(message);
                return Task.FromResult(answer);
            };
            var changed = 0;
            model.CurrentDatabaseChanged += () => changed++;

            model.IntoCurrentDatabase = true;
            await model.StartCommand.ExecuteAsync(null);

            await Assert.That(asked.Single()).Contains("\"shop\" on db.example.com");
            await Assert.That(service.Runs).IsEmpty();
            await Assert.That(model.State).IsEqualTo(RestoreWindowState.Ready);

            answer = true;
            await model.StartCommand.ExecuteAsync(null);
            Ui.Settle();

            await Assert.That(service.Runs.Single().Target).IsEqualTo(RestoreTarget.CurrentDatabase);
            await Assert.That(service.Runs.Single().DatabaseName).IsEqualTo("shop");
            // The schema tree behind it is out of date now.
            await Assert.That(changed).IsEqualTo(1);
            await Assert.That(model.CanOpenDatabase).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task The_window_wires_the_confirmation_to_a_real_dialog()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeRestoreService();
            var model = Model(service);
            var window = await OpenAsync(model);
            await InspectAsync(model);
            model.IntoCurrentDatabase = true;

            var running = model.StartCommand.ExecuteAsync(null);
            Ui.Settle();
            var confirm = window.OwnedWindows.OfType<ConfirmDialog>().Single();
            await Assert.That((string?)confirm.FindControl<Button>("ConfirmButton")!.Content).IsEqualTo("Restore and Replace");
            confirm.Close(false);
            await running;

            await Assert.That(service.Runs).IsEmpty();
            window.Close();
        });
    }

    [Test]
    public async Task A_taken_or_empty_name_disables_Restore()
    {
        await Ui.Run(async () =>
        {
            var model = Model(new FakeRestoreService());
            var window = await OpenAsync(model);
            await InspectAsync(model);

            model.NewDatabaseName = "postgres";
            await Assert.That(await Ui.WaitUntilAsync(() => model.NameError is not null)).IsTrue();
            await Assert.That(model.NameError).IsEqualTo("There's already a database called postgres.");
            await Assert.That(model.StartCommand.CanExecute(null)).IsFalse();

            model.NewDatabaseName = "";
            await Assert.That(model.NameError).IsEqualTo("Give the new database a name.");

            // Into the current database the name doesn't matter.
            model.IntoCurrentDatabase = true;
            await Assert.That(model.StartCommand.CanExecute(null)).IsTrue();

            window.Close();
        });
    }

    [Test]
    public async Task A_script_is_refused_with_the_reason_and_no_Restore_button()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeRestoreService
            {
                Inspection = new RestoreInspection(PgArchiveFormat.PlainSql, null, "This is a SQL script … psql …"),
            };
            var model = Model(service);
            var window = await OpenAsync(model);
            await InspectAsync(model);

            await Assert.That(model.State).IsEqualTo(RestoreWindowState.Unusable);
            await Assert.That(window.FindControl<Border>("UnusableCard")!.IsVisible).IsTrue();
            await Assert.That(window.FindControl<Button>("StartButton")!.IsEffectivelyVisible).IsFalse();
            await Assert.That(model.StartCommand.CanExecute(null)).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task A_failure_says_what_was_left_behind_and_what_to_do()
    {
        await Ui.Run(async () =>
        {
            var service = new FakeRestoreService
            {
                Run = (_, plan, _, _) => Task.FromResult(new RestoreResult(
                    RestoreOutcome.Failed, plan.DatabaseName, TimeSpan.FromSeconds(1),
                    "could not execute query: ERROR:  role \"reporting\" does not exist",
                    "The backup names a role this server doesn't have.", "", CreatedDatabaseRemoved: true)),
            };
            var model = Model(service);
            var window = await OpenAsync(model);
            await InspectAsync(model);
            model.KeepOwners = true;

            await model.StartCommand.ExecuteAsync(null);
            Ui.Settle();

            await Assert.That(model.State).IsEqualTo(RestoreWindowState.Failed);
            await Assert.That(model.ResultDetail).Contains("role \"reporting\" does not exist");
            await Assert.That(model.ResultDetail).Contains("the new database shop_restored was removed again");
            await Assert.That(model.ResultHint).IsEqualTo("The backup names a role this server doesn't have.");

            model.TryAgainCommand.Execute(null);
            await Assert.That(model.State).IsEqualTo(RestoreWindowState.Ready);
            await Assert.That(model.KeepOwners).IsTrue();

            window.Close();
        });
    }

    [Test]
    public async Task A_new_database_can_be_opened_in_a_window_of_its_own()
    {
        await Ui.Run(async () =>
        {
            var opened = new List<string>();
            var model = Model(new FakeRestoreService(), database =>
            {
                opened.Add(database);
                return Task.CompletedTask;
            });
            var window = await OpenAsync(model);
            await InspectAsync(model);

            await model.StartCommand.ExecuteAsync(null);
            Ui.Settle();
            await Assert.That(window.FindControl<Button>("OpenDatabaseButton")!.IsEffectivelyVisible).IsTrue();
            await model.OpenDatabaseCommand.ExecuteAsync(null);

            await Assert.That(opened).IsEquivalentTo(["shop_restored"]);
            window.Close();
        });
    }

    [Test]
    public async Task A_backup_from_a_newer_release_is_flagged()
    {
        await Ui.Run(async () =>
        {
            var model = Model(new FakeRestoreService { ServerVersion = new PgVersion(16, 4) });
            var window = await OpenAsync(model);
            await InspectAsync(model);

            await Assert.That(model.VersionWarning).Contains("PostgreSQL 17, and this server runs 16");
            window.Close();
        });
    }

    [Test]
    public async Task The_palette_command_opens_one_restore_window_and_a_read_only_connection_says_why_not()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.RestoreBackupCommand.Execute(null);
            vm.RestoreBackupCommand.Execute(null);
            Ui.Settle();
            var restore = window.OwnedWindows.OfType<RestoreWindow>().Single();
            restore.Close();

            var readOnly = Fixtures.MainWindowViewModel(readOnlyProfile: true);
            var requested = false;
            readOnly.RestoreRequested += () => requested = true;
            readOnly.RestoreBackupCommand.Execute(null);
            await Assert.That(requested).IsFalse();
            await Assert.That(readOnly.ActiveTab.Status).Contains("read-only");

            window.Close();
        });
    }
}
