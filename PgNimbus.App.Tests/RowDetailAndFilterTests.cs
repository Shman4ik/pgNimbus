using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The row-detail sidebar and browse filters (README roadmap T4). What has to
/// hold: sidebar edits go through the staged set whatever safe mode says (so
/// the existing review dialog and conflict-checked commit apply to them), a
/// bad value stages nothing, filters compose server-side SQL through the same
/// page query browse mode runs, and a hand-written query is never rewritten by
/// a filter gesture.
/// </summary>
public class RowDetailAndFilterTests
{
    [Test]
    public async Task Row_details_opens_on_its_chord_and_shows_the_selected_row()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var tab = SeedEditableRow(vm);

            Ui.Press(window, CommandId.RowDetails);

            await Assert.That(vm.IsRowDetailOpen).IsTrue();
            // Opened with nothing selected, the sidebar takes the first row.
            await Assert.That(tab.RowDetail.Row).IsSameReferenceAs(tab.Rows[0]);
            var fields = tab.RowDetail.Fields;
            await Assert.That(fields.Select(f => f.Name)).IsEquivalentTo(new[] { "id", "status", "qty" });
            await Assert.That(fields[0].IsEditable).IsFalse();
            await Assert.That(fields[0].ReadOnlyReason).IsEqualTo("primary key");
            await Assert.That(fields[1].Editor!.Value).IsEqualTo("packed");

            Ui.Press(window, CommandId.RowDetails);
            await Assert.That(vm.IsRowDetailOpen).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task Sidebar_edits_are_staged_even_with_safe_mode_off()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.SafeModeEdits = false;
            var tab = SeedEditableRow(vm);
            tab.RowDetail.Load(tab.Rows[0]);

            tab.RowDetail.Fields[1].Editor!.Value = "shipped";
            tab.RowDetail.Fields[2].Editor!.IsNull = true;
            await Assert.That(tab.RowDetail.ChangedCount).IsEqualTo(2);

            tab.RowDetail.StageCommand.Execute(null);

            // Nothing was executed (the fixture's server is unroutable): both
            // edits sit in the tab's staged set, for the usual review + commit.
            await Assert.That(tab.HasPendingChanges).IsTrue();
            var edits = tab.PendingChanges!.GetRowEdits([1L])!.ToDictionary(e => e.Column, e => e.Value);
            await Assert.That(edits["status"]).IsEqualTo("shipped");
            await Assert.That(edits.ContainsKey("qty")).IsTrue();
            await Assert.That(edits["qty"]).IsNull();
            // The snapshot is the row as it was before the sidebar touched it.
            await Assert.That(tab.PendingChanges!.GetOriginal([1L])!.Values[1]).IsEqualTo("packed");
            await Assert.That(tab.Rows[0][1]).IsEqualTo("shipped");
            // The sidebar now shows the staged row, with nothing left unstaged.
            await Assert.That(tab.RowDetail.Row).IsSameReferenceAs(tab.Rows[0]);
            await Assert.That(tab.RowDetail.ChangedCount).IsEqualTo(0);

            window.Close();
        });
    }

    [Test]
    public async Task A_bad_value_in_the_sidebar_stages_nothing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var tab = SeedEditableRow(vm);
            tab.RowDetail.Load(tab.Rows[0]);

            tab.RowDetail.Fields[1].Editor!.Value = "shipped";
            tab.RowDetail.Fields[2].Editor!.Value = "lots";
            tab.RowDetail.StageCommand.Execute(null);

            await Assert.That(tab.HasPendingChanges).IsFalse();
            await Assert.That(tab.RowDetail.Error).Contains("qty");
            await Assert.That(tab.Rows[0][1]).IsEqualTo("packed");
            // The typed values survive the refusal, to be fixed rather than retyped.
            await Assert.That(tab.RowDetail.ChangedCount).IsEqualTo(2);

            window.Close();
        });
    }

    [Test]
    public async Task Moving_the_selection_does_not_discard_unstaged_sidebar_edits()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var tab = SeedEditableRow(vm, rows: 2);
            tab.RowDetail.Load(tab.Rows[0]);
            tab.RowDetail.Fields[1].Editor!.Value = "shipped";

            tab.RowDetail.Load(tab.Rows[1]);
            await Assert.That(tab.RowDetail.Row).IsSameReferenceAs(tab.Rows[0]);

            tab.RowDetail.RevertCommand.Execute(null);
            await Assert.That(tab.RowDetail.ChangedCount).IsEqualTo(0);
            tab.RowDetail.Load(tab.Rows[1]);
            await Assert.That(tab.RowDetail.Row).IsSameReferenceAs(tab.Rows[1]);

            window.Close();
        });
    }

    [Test]
    public async Task Filtering_a_hand_written_query_rewrites_nothing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var tab = vm.ActiveTab;
            const string sql = "SELECT * FROM orders WHERE total > 10;";
            tab.Sql = sql;

            vm.FilterRowsCommand.Execute(null);

            await Assert.That(tab.Sql).IsEqualTo(sql);
            await Assert.That(tab.Browse).IsNull();
            await Assert.That(tab.Status).Contains("never rewritten");
            // And no filter bar exists to apply to it.
            var bar = window.GetVisualDescendants().OfType<BrowseFilterBar>().Single();
            await Assert.That(bar.IsEffectivelyVisible).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task Filters_compose_a_server_side_where_and_only_apply_on_request()
    {
        var (browse, executed) = Browse();

        var filter = browse.AddFilter("total", FilterOperator.GreaterOrEqual, "10.5")!;
        browse.AddFilter("shipped_at", FilterOperator.IsNull);
        // Editing composes a preview, and runs nothing.
        await Assert.That(executed).IsEmpty();
        await Assert.That(browse.FilterPreviewSql).IsEqualTo("WHERE (\"total\" >= '10.5')\n  AND (\"shipped_at\" IS NULL)");

        browse.ApplyFiltersCommand.Execute(null);

        await Assert.That(executed).Count().IsEqualTo(1);
        await Assert.That(executed[0]).Contains("WHERE (\"total\" >= '10.5')\n  AND (\"shipped_at\" IS NULL)");
        await Assert.That(executed[0]).EndsWith("LIMIT 100 OFFSET 0");
        await Assert.That(browse.HasActiveFilters).IsTrue();
        await Assert.That(browse.IsFilterBarVisible).IsTrue();

        // A half-typed change stays a draft: paging keeps running what was applied.
        filter.Value.Value = "99";
        browse.Offset = 100;
        await browse.LoadAsync();
        await Assert.That(executed[^1]).Contains("\"total\" >= '10.5'");
    }

    [Test]
    public async Task An_invalid_filter_is_refused_as_a_whole()
    {
        var (browse, executed) = Browse();

        browse.AddFilter("status", FilterOperator.Equals, "packed");
        browse.AddFilter("total", FilterOperator.Greater, "lots");
        browse.ApplyFiltersCommand.Execute(null);

        await Assert.That(executed).IsEmpty();
        await Assert.That(browse.FilterError).Contains("total");
        await Assert.That(browse.HasActiveFilters).IsFalse();
    }

    [Test]
    public async Task Removing_a_filter_reapplies_and_clearing_drops_the_fk_condition_too()
    {
        var (browse, executed) = Browse();
        browse.FilterText = "\"customer_id\" = 7";
        var status = browse.AddFilter("status", FilterOperator.Equals, "packed")!;
        browse.ApplyFiltersCommand.Execute(null);
        await Assert.That(executed[^1]).Contains("WHERE (\"customer_id\" = 7)\n  AND (\"status\" = 'packed')");

        browse.RemoveFilterCommand.Execute(status);
        await Assert.That(executed[^1]).Contains("WHERE \"customer_id\" = 7\n");

        browse.ClearFiltersCommand.Execute(null);
        await Assert.That(executed[^1]).DoesNotContain("WHERE");
        await Assert.That(browse.IsFilterBarVisible).IsFalse();
    }

    [Test]
    public async Task A_filter_on_an_enum_uses_its_dropdown_and_offers_only_what_enums_support()
    {
        var (browse, executed) = Browse();

        var filter = browse.AddFilter("status")!;

        await Assert.That(filter.Operators.Select(o => o.Operator))
            .IsEquivalentTo(new[] { FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.IsNull, FilterOperator.IsNotNull });
        await Assert.That(filter.Value.IsEnumEditor).IsTrue();
        filter.Value.EnumChoice = "shipped";
        await browse.AddAndApplyFilterAsync("total", FilterOperator.IsNotNull, null);

        await Assert.That(executed[^1]).Contains("(\"status\" = 'shipped')");
        await Assert.That(executed[^1]).Contains("(\"total\" IS NOT NULL)");
    }

    // A browse view model over a fake table whose "execute" records the SQL
    // instead of running it — the composition is what's under test here.
    private static (TableBrowseViewModel Browse, List<string> Executed) Browse()
    {
        var executed = new List<string>();
        var browse = new TableBrowseViewModel(
            "public",
            "orders",
            [
                new ColumnDetail("id", "bigint", NotNull: true, IsPrimaryKey: true),
                new ColumnDetail("customer_id", "bigint", NotNull: true, IsPrimaryKey: false),
                new ColumnDetail("status", "order_status", NotNull: true, IsPrimaryKey: false)
                {
                    Editor = ColumnValueEditor.Enum,
                    EnumLabels = ["packed", "shipped"],
                },
                new ColumnDetail("total", "numeric(10,2)", NotNull: false, IsPrimaryKey: false),
                new ColumnDetail("shipped_at", "timestamp with time zone", NotNull: false, IsPrimaryKey: false)
                {
                    Editor = ColumnValueEditor.Timestamp,
                },
            ],
            sql =>
            {
                executed.Add(sql);
                return Task.FromResult(0);
            });
        return (browse, executed);
    }

    private static QueryViewModel SeedEditableRow(MainViewModel vm, int rows = 1)
    {
        var tab = vm.ActiveTab;
        tab.SeedResult(
            [
                new ColumnInfo("id", "bigint", typeof(long)),
                new ColumnInfo("status", "text", typeof(string)),
                new ColumnInfo("qty", "integer", typeof(int)),
            ],
            Enumerable.Range(1, rows).Select(i => new object?[] { (long)i, "packed", 3 }).ToList());
        tab.EditContext = new EditableTableContext(
            "public", "orders", ["id"],
            [new ColumnDetail("id", "bigint", NotNull: true, IsPrimaryKey: true),
             new ColumnDetail("status", "text", NotNull: false, IsPrimaryKey: false),
             new ColumnDetail("qty", "integer", NotNull: false, IsPrimaryKey: false)]);
        Ui.Settle();
        return tab;
    }
}
