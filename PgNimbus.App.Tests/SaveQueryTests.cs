using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// How a query gets into the Saved Queries list. Every assertion here encodes a
/// piece of the confusion this feature was reported for: the save gesture went
/// to the wrong destination, there was no route to the list except one text box
/// in the sidebar, and saving twice made duplicates.
/// </summary>
public class SaveQueryTests
{
    /// <summary>
    /// The headline fix. Ctrl+S used to be wired to the .sql file picker
    /// outright, so pressing it on an ordinary tab opened a file dialog and left
    /// the Saved Queries list — visible in the sidebar at the time — untouched.
    /// It now follows the tab, and a scratch tab means the list.
    /// </summary>
    [Test]
    public async Task Save_on_a_scratch_tab_targets_the_saved_queries_list()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var toQuery = 0;
            var toFile = 0;
            vm.SaveQueryRequested += _ => toQuery++;
            vm.SaveFileRequested += _ => toFile++;

            Ui.Press(window, CommandId.Save);

            await Assert.That(vm.ActiveTab.FilePath).IsNull();
            await Assert.That(toQuery).IsEqualTo(1);
            await Assert.That(toFile).IsEqualTo(0);

            window.Close();
        });
    }

    /// <summary>
    /// The other half of the same rule: a tab opened from disk still saves back
    /// to its file, so the smart routing does not cost file users their Ctrl+S.
    /// </summary>
    [Test]
    public async Task Save_on_a_file_backed_tab_still_targets_the_file()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            vm.ActiveTab.AttachFile(Path.Combine(Path.GetTempPath(), "report.sql"), vm.ActiveTab.Sql);

            var toQuery = 0;
            var toFile = 0;
            vm.SaveQueryRequested += _ => toQuery++;
            vm.SaveFileRequested += _ => toFile++;

            Ui.Press(window, CommandId.Save);

            await Assert.That(toFile).IsEqualTo(1);
            await Assert.That(toQuery).IsEqualTo(0);

            window.Close();
        });
    }

    /// <summary>
    /// Saving the same tab twice used to mint a fresh id each time, leaving two
    /// rows with one name that no part of the UI could tell apart. Re-saving now
    /// writes through the tab's own entry.
    /// </summary>
    [Test]
    public async Task Re_saving_a_tab_updates_its_entry_instead_of_duplicating_it()
    {
        await Ui.Run(async () =>
        {
            var (_, vm) = Scenarios.Shell();
            var saved = vm.SavedQueries;
            var before = saved.SavedQueries.Count;

            var first = saved.SaveQuery("Daily report", "SELECT 1;");
            var second = saved.SaveQuery("Daily report", "SELECT 2;", first.Id);

            await Assert.That(saved.SavedQueries).Count().IsEqualTo(before + 1);
            await Assert.That(second.Id).IsEqualTo(first.Id);
            await Assert.That(saved.FindById(first.Id)!.Sql).IsEqualTo("SELECT 2;");
        });
    }

    /// <summary>
    /// The name lookup the save dialog uses to offer a replace. It is
    /// case-insensitive because the list is read by eye: "Daily report" and
    /// "daily report" sitting next to each other is the duplicate problem
    /// wearing a different hat.
    /// </summary>
    [Test]
    public async Task A_name_already_in_the_list_is_found_whatever_its_casing()
    {
        await Ui.Run(async () =>
        {
            var (_, vm) = Scenarios.Shell();
            var saved = vm.SavedQueries;

            var entry = saved.SaveQuery("Daily Report", "SELECT 1;");

            await Assert.That(saved.FindByName("daily report")?.Id).IsEqualTo(entry.Id);
            await Assert.That(saved.FindByName("  Daily Report  ")?.Id).IsEqualTo(entry.Id);
            await Assert.That(saved.FindByName("nothing like it")).IsNull();
        });
    }

    /// <summary>
    /// The saved name is a name a person chose, so it must survive later edits
    /// to the SQL — the distinction CLAUDE.md draws between a TitleOverride and
    /// a DefaultTitle. A label would be replaced the moment the buffer changed.
    /// </summary>
    [Test]
    public async Task Saving_names_the_tab_and_the_name_survives_an_edit()
    {
        await Ui.Run(async () =>
        {
            var (_, vm) = Scenarios.Shell();
            var tab = vm.ActiveTab;

            tab.MarkSavedAsQuery(Guid.NewGuid(), "Daily report");
            tab.Sql = "SELECT * FROM commerce.products;";

            await Assert.That(tab.TabTitle).IsEqualTo("Daily report");
        });
    }
}
