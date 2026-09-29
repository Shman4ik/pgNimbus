using PgNimbus.Core.Settings;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Restoring a workspace reattaches each tab to the file it was saved with.
/// That read used to be a synchronous <c>File.ReadAllText</c> on the UI thread
/// in <c>MainViewModel</c>'s constructor, so a file on a stale network path held
/// the window for the SMB timeout at every launch, and it caught only IO and
/// access errors, so a path with a NUL in it (a tampered or damaged
/// <c>workspace.json</c>) crashed startup on every launch (security audit
/// 2026-09, finding 18).
/// </summary>
public class WorkspaceRestoreTests
{
    [Test]
    public async Task Restored_tabs_reattach_their_files_off_the_ui_thread_and_bad_paths_do_not_crash_startup()
    {
        await Ui.Run(async () =>
        {
            var dir = IsolatedAppData.NewDirectory("workspace-restore");
            Directory.CreateDirectory(dir);
            var onDisk = Path.Combine(dir, "report.sql");
            await File.WriteAllTextAsync(onDisk, "SELECT 1;");

            var workspace = new WorkspaceEntry("localhost/shop", DateTimeOffset.UtcNow,
            [
                new WorkspaceTab("SELECT 1;", "report.sql", onDisk),
                new WorkspaceTab("SELECT 2;", "Gone", Path.Combine(dir, "missing.sql")),
                new WorkspaceTab("SELECT 3;", "Nul", "bad\0path.sql"),
                new WorkspaceTab("SELECT 4;", "Device", "\\\\?\\"),
            ]);

            // Building the view model must not throw whatever the paths hold.
            var vm = Fixtures.MainWindowViewModel(workspace);
            await Assert.That(vm.Tabs).Count().IsEqualTo(4);

            await vm.WorkspaceFilesRestored.WaitAsync(TimeSpan.FromSeconds(10));
            Ui.Settle();

            // The readable file is attached: same content, so not dirty.
            await Assert.That(vm.Tabs[0].FilePath).IsEqualTo(onDisk);
            await Assert.That(vm.Tabs[0].IsDirty).IsFalse();

            // The rest stay titled scratch tabs with their restored text.
            for (var i = 1; i < 4; i++)
            {
                await Assert.That(vm.Tabs[i].FilePath).IsNull();
                await Assert.That(vm.Tabs[i].Sql).IsEqualTo($"SELECT {i + 1};");
            }

            await Assert.That(vm.Tabs[1].TabTitle).IsEqualTo("Gone");
            await Assert.That(vm.Tabs[2].TabTitle).IsEqualTo("Nul");
        });
    }

    [Test]
    public async Task A_file_tab_whose_text_was_not_kept_is_read_from_the_file_and_opens_clean()
    {
        // The snapshot keeps no text for a file tab that held a password; the
        // restore reads it back, so the tab is not modified and Save cannot
        // write a placeholder over the file (review of the 2026-09 audit fixes).
        await Ui.Run(async () =>
        {
            var dir = IsolatedAppData.NewDirectory("workspace-restore-secret");
            Directory.CreateDirectory(dir);
            var onDisk = Path.Combine(dir, "001_roles.sql");
            await File.WriteAllTextAsync(onDisk, "CREATE ROLE app LOGIN PASSWORD 'hunter2';");

            var vm = Fixtures.MainWindowViewModel(new WorkspaceEntry("localhost/shop", DateTimeOffset.UtcNow,
            [
                new WorkspaceTab("", "001_roles.sql", onDisk, TextFromFile: true),
                new WorkspaceTab("", "gone.sql", Path.Combine(dir, "gone.sql"), TextFromFile: true),
            ]));

            await vm.WorkspaceFilesRestored.WaitAsync(TimeSpan.FromSeconds(10));
            Ui.Settle();

            await Assert.That(vm.Tabs[0].Sql).IsEqualTo("CREATE ROLE app LOGIN PASSWORD 'hunter2';");
            await Assert.That(vm.Tabs[0].IsDirty).IsFalse();

            // A file that is gone leaves a note saying why the tab is empty.
            await Assert.That(vm.Tabs[1].Sql).Contains("did not keep a copy");
            await Assert.That(vm.Tabs[1].FilePath).IsNull();
        });
    }

    [Test]
    public async Task A_workspace_with_no_files_is_restored_at_once()
    {
        await Ui.Run(async () =>
        {
            var vm = Fixtures.MainWindowViewModel(new WorkspaceEntry("localhost/shop", DateTimeOffset.UtcNow,
                [new WorkspaceTab("SELECT 1;", "one")]));

            await Assert.That(vm.WorkspaceFilesRestored.IsCompleted).IsTrue();
            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
        });
    }
}
