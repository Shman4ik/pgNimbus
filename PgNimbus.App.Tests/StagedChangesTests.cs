using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Safe mode's conflict handling from the UI side: that staging records the row
/// as the user saw it (the thing the commit-time check compares against), that
/// a row with no usable identity is refused rather than staged, and that the
/// conflict dialog lays out before / current / proposed and opens and closes.
/// The check itself, against a real second session, is in
/// <c>PgNimbus.Core.Tests</c>' <c>QueryEngineStagedConflictTests</c>.
/// </summary>
public class StagedChangesTests
{
    [Test]
    public async Task Staging_an_edit_snapshots_the_row_before_showing_the_staged_value()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.SafeModeEdits = true;
            var tab = SeedEditableRow(vm, id: 1L, status: "packed");

            var staged = await tab.CommitCellEditAsync(tab.Rows[0], 1, "shipped");

            await Assert.That(staged).IsTrue();
            await Assert.That(tab.Rows[0][1]).IsEqualTo("shipped");
            var original = tab.PendingChanges!.GetOriginal([1L])!;
            await Assert.That(original.Columns).IsEquivalentTo(new[] { "id", "status" }, CollectionOrdering.Matching);
            await Assert.That(original.Values[1]).IsEqualTo("packed");

            // A second edit of the same row must not replace the snapshot with
            // one that already shows the first staged value.
            await tab.CommitCellEditAsync(tab.Rows[0], 1, "delivered");
            await Assert.That(tab.PendingChanges!.GetOriginal([1L])!.Values[1]).IsEqualTo("packed");

            window.Close();
        });
    }

    [Test]
    public async Task A_row_whose_key_could_not_be_read_is_not_staged()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.SafeModeEdits = true;
            var tab = SeedEditableRow(vm, id: QueryEngine.UnreadableCell("public.order_ref"), status: "packed");

            var staged = await tab.CommitCellEditAsync(tab.Rows[0], 1, "shipped");

            await Assert.That(staged).IsFalse();
            await Assert.That(tab.HasPendingChanges).IsFalse();
            await Assert.That(tab.HasError).IsTrue();
            await Assert.That(tab.Status).Contains("can't be identified exactly");
            await Assert.That(tab.Rows[0][1]).IsEqualTo("packed");

            window.Close();
        });
    }

    [Test]
    public async Task Conflict_dialog_shows_each_row_before_current_and_proposed()
    {
        await Ui.Run(async () =>
        {
            var set = new PendingChangeSet("sales", "order_lines", ["order_id", "line"]);
            set.StageEdit([7, 1], "qty", 5, original: new RowSnapshot(["order_id", "line", "qty", "note"], [7, 1, 2, null]));
            set.StageDelete([7, 2], new RowSnapshot(["order_id", "line", "qty", "note"], [7, 2, 3, null]));
            var conflicts = set.BuildRowCheck()!.Evaluate(["order_id", "line", "qty", "note"], [[7, 1, 2, "rush"]]);

            var dialog = new StagedConflictDialog(new StagedChangesConflictException(conflicts));
            Ui.Show(dialog);

            var rows = (IReadOnlyList<ConflictRowView>)dialog.FindControl<ItemsControl>("ConflictList")!.ItemsSource!;
            await Assert.That(rows).Count().IsEqualTo(2);
            await Assert.That(rows[0].Header).IsEqualTo("Row order_id = 7, line = 1");

            var note = rows[0].Cells.Single(c => c.Column == "note");
            await Assert.That(note.Before).IsEqualTo("NULL");
            await Assert.That(note.Current).IsEqualTo("'rush'");
            await Assert.That(note.ChangedElsewhere).IsTrue();
            await Assert.That(rows[0].Cells.Single(c => c.Column == "qty").Proposed).IsEqualTo("5");
            await Assert.That(rows[1].Cells[0].Current).IsEqualTo("(row gone)");

            // The changed column is the one that draws attention.
            var highlighted = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("changed")).ToList();
            await Assert.That(highlighted.Any(t => t.Text == "'rush'")).IsTrue();

            await Assert.That(dialog.FindControl<Button>("RestageButton")!.IsVisible).IsTrue();
            dialog.Close();
        });
    }

    [Test]
    public async Task A_lock_conflict_offers_no_restage_because_it_names_no_rows()
    {
        await Ui.Run(async () =>
        {
            var dialog = new StagedConflictDialog(StagedChangesConflictException.Locked(new TimeoutException()));
            Ui.Show(dialog);

            await Assert.That(dialog.FindControl<Button>("RestageButton")!.IsVisible).IsFalse();
            await Assert.That(dialog.FindControl<Button>("UnstageButton")!.IsVisible).IsFalse();
            await Assert.That(dialog.FindControl<SelectableTextBlock>("MessageText")!.Text).Contains("locked by another session");

            dialog.Close();
        });
    }

    private static QueryViewModel SeedEditableRow(MainViewModel vm, object id, string status)
    {
        var tab = vm.ActiveTab;
        tab.SeedResult(
            [new ColumnInfo("id", "bigint", typeof(long)), new ColumnInfo("status", "text", typeof(string))],
            [[id, status]]);
        tab.EditContext = new EditableTableContext(
            "public", "orders", ["id"],
            [new ColumnDetail("id", "bigint", NotNull: true, IsPrimaryKey: true),
             new ColumnDetail("status", "text", NotNull: false, IsPrimaryKey: false)]);
        Ui.Settle();
        return tab;
    }
}
