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
/// Row details and browse filter chips (README roadmap T4). What has to hold: row-detail edits go through the staged
/// set whatever safe mode says (so
/// the existing review dialog and conflict-checked commit apply to them), a
/// bad value stages nothing, conditions compose server-side SQL through the
/// same page query browse mode runs (and nothing runs until one is applied), a
/// hand-written query is never rewritten by a filter gesture, and a WHERE typed
/// into a browse tab comes back as chips that account for all of it.
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
            await Assert.That(tab.RowDetail.Heading).IsEqualTo("Row 1 of 1");
            var fields = tab.RowDetail.Fields;
            await Assert.That(fields.Select(f => f.Name)).IsEquivalentTo(new[] { "id", "status", "qty" });
            await Assert.That(fields[0].IsEditable).IsFalse();
            // Said once, in the type line, not again as a note under the value.
            await Assert.That(fields[0].TypeLabel).IsEqualTo("bigint · primary key");
            await Assert.That(fields[0].ReadOnlyReason).IsNull();
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
    public async Task Previous_and_next_walk_the_rows_but_not_away_from_unstaged_edits()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var tab = SeedEditableRow(vm, rows: 3);
            object?[]? requested = null;
            tab.RowDetail.NavigateRequested += row => requested = row;
            tab.RowDetail.Load(tab.Rows[1]);

            await Assert.That(tab.RowDetail.PreviousRowCommand.CanExecute(null)).IsTrue();
            tab.RowDetail.NextRowCommand.Execute(null);
            await Assert.That(requested).IsSameReferenceAs(tab.Rows[2]);

            tab.RowDetail.Load(tab.Rows[2]);
            await Assert.That(tab.RowDetail.NextRowCommand.CanExecute(null)).IsFalse();

            tab.RowDetail.Fields[1].Editor!.Value = "shipped";
            await Assert.That(tab.RowDetail.PreviousRowCommand.CanExecute(null)).IsFalse();

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
            // And no chip strip exists to apply to it.
            var bar = window.GetVisualDescendants().OfType<BrowseFilterBar>().Single();
            await Assert.That(bar.IsEffectivelyVisible).IsFalse();

            window.Close();
        });
    }

    [Test]
    public async Task A_draft_condition_previews_its_sql_and_runs_nothing_until_applied()
    {
        var (browse, executed) = Browse();

        var draft = browse.BeginNewFilter("total", FilterOperator.GreaterOrEqual, "10.5")!;
        await Assert.That(browse.DraftPreviewSql).IsEqualTo("\"total\" >= '10.5'");
        await Assert.That(browse.IsFilterBarVisible).IsTrue();
        await Assert.That(executed).IsEmpty();
        await Assert.That(browse.Filters).IsEmpty();

        browse.CommitDraftCommand.Execute(null);

        await Assert.That(browse.Filters).Count().IsEqualTo(1);
        await Assert.That(browse.Filters[0].Summary).IsEqualTo("total ≥ 10.5");
        await Assert.That(browse.Draft).IsNull();
        await Assert.That(executed).Count().IsEqualTo(1);
        await Assert.That(executed[0]).Contains("WHERE \"total\" >= '10.5'");
        await Assert.That(executed[0]).EndsWith("LIMIT 100 OFFSET 0");
    }

    [Test]
    public async Task Conditions_combine_and_paging_keeps_them()
    {
        var (browse, executed) = Browse();

        await browse.AddAndApplyFilterAsync("total", FilterOperator.GreaterOrEqual, "10.5");
        await browse.AddAndApplyFilterAsync("shipped_at", FilterOperator.IsNull, null);
        await Assert.That(browse.WhereSql).IsEqualTo("WHERE (\"total\" >= '10.5') AND (\"shipped_at\" IS NULL)");

        browse.Offset = 100;
        await browse.LoadAsync();
        await Assert.That(executed[^1]).Contains("WHERE (\"total\" >= '10.5')\n  AND (\"shipped_at\" IS NULL)");
        await Assert.That(executed[^1]).EndsWith("LIMIT 100 OFFSET 100");
    }

    [Test]
    public async Task An_invalid_draft_is_refused_and_stays_open()
    {
        var (browse, executed) = Browse();

        browse.BeginNewFilter("total", FilterOperator.Greater, "lots");
        browse.CommitDraftCommand.Execute(null);

        await Assert.That(executed).IsEmpty();
        await Assert.That(browse.FilterError).Contains("total");
        await Assert.That(browse.Filters).IsEmpty();
        await Assert.That(browse.Draft).IsNotNull();
    }

    [Test]
    public async Task Editing_a_chip_works_on_a_copy_and_replaces_it_on_apply()
    {
        var (browse, executed) = Browse();
        await browse.AddAndApplyFilterAsync("total", FilterOperator.Greater, "10");
        var chip = browse.Filters[0];

        var draft = browse.BeginEditFilter(chip);
        draft.Value.Value = "99";
        // Until Apply, the chip — and so the query — is untouched.
        await Assert.That(chip.Summary).IsEqualTo("total > 10");
        await Assert.That(browse.IsEditingExisting).IsTrue();

        browse.CommitDraftCommand.Execute(null);
        await Assert.That(browse.Filters).Count().IsEqualTo(1);
        await Assert.That(browse.Filters[0].Summary).IsEqualTo("total > 99");
        await Assert.That(executed[^1]).Contains("\"total\" > '99'");
    }

    [Test]
    public async Task Abandoning_a_draft_leaves_the_query_and_the_strip_as_they_were()
    {
        var (browse, executed) = Browse();

        browse.BeginNewFilter("status");
        browse.CancelDraft();

        await Assert.That(executed).IsEmpty();
        await Assert.That(browse.IsFilterBarVisible).IsFalse();
    }

    [Test]
    public async Task Removing_a_chip_reapplies_and_clearing_drops_the_fk_condition_too()
    {
        var (browse, executed) = Browse();
        browse.FilterText = "\"customer_id\" = 7";
        await browse.AddAndApplyFilterAsync("status", FilterOperator.Equals, "packed");
        await Assert.That(executed[^1]).Contains("WHERE (\"customer_id\" = 7)\n  AND (\"status\" = 'packed')");

        browse.RemoveFilterCommand.Execute(browse.Filters[0]);
        await Assert.That(executed[^1]).Contains("WHERE \"customer_id\" = 7\n");

        browse.ClearFiltersCommand.Execute(null);
        await Assert.That(executed[^1]).DoesNotContain("WHERE");
        await Assert.That(browse.IsFilterBarVisible).IsFalse();
    }

    [Test]
    public async Task A_condition_on_an_enum_uses_its_dropdown_and_offers_only_what_enums_support()
    {
        var (browse, executed) = Browse();

        var draft = browse.BeginNewFilter("status")!;

        await Assert.That(draft.Operators.Select(o => o.Operator))
            .IsEquivalentTo(new[] { FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.IsNull, FilterOperator.IsNotNull });
        await Assert.That(draft.Value.IsEnumEditor).IsTrue();
        draft.Value.EnumChoice = "shipped";
        browse.CommitDraftCommand.Execute(null);

        await Assert.That(executed[^1]).Contains("WHERE \"status\" = 'shipped'");
    }

    [Test]
    public async Task A_parsed_where_comes_back_as_chips_without_running_anything()
    {
        var (_, executed) = Browse();
        var columns = Browse().Browse.Columns;
        var shape = BrowseSqlParser.TryParse(
            "SELECT * FROM orders WHERE total > 6 AND (status = 'packed' OR total IS NULL) ORDER BY total DESC LIMIT 25 OFFSET 50",
            "public", "orders", columns)!;

        var browse = TableBrowseViewModel.FromParsed("public", "orders", columns, shape, rowCount: 25, sql =>
        {
            executed.Add(sql);
            return Task.FromResult(25);
        });

        await Assert.That(executed).IsEmpty();
        await Assert.That(browse.Filters.Select(f => f.Summary)).IsEquivalentTo(new[] { "total > 6" });
        await Assert.That(browse.RawConditions).IsEquivalentTo(new[] { "status = 'packed' OR total IS NULL" });
        await Assert.That(browse.IsFilterBarVisible).IsTrue();
        await Assert.That(browse.PageSize).IsEqualTo(25);
        await Assert.That(browse.PageLabel).IsEqualTo("Rows 51–75");
        await Assert.That(browse.CanGoNext).IsTrue();

        // The next explicit action composes the query again, keeping all of it.
        browse.NextPageCommand.Execute(null);
        await Assert.That(executed[^1]).IsEqualTo(
            "SELECT * FROM \"public\".\"orders\"\nWHERE (status = 'packed' OR total IS NULL)\n  AND (\"total\" > '6')\nORDER BY \"total\" DESC\nLIMIT 25 OFFSET 75");
    }

    [Test]
    public async Task Removing_a_raw_chip_keeps_the_others()
    {
        var (browse, executed) = Browse();
        browse.RawConditions.Add("a = 1 OR b = 2");
        browse.RawConditions.Add("c IN (1, 2)");

        browse.ClearRawFilterCommand.Execute("a = 1 OR b = 2");

        await Assert.That(browse.RawConditions).IsEquivalentTo(new[] { "c IN (1, 2)" });
        await Assert.That(executed[^1]).Contains("WHERE c IN (1, 2)\n");
    }

    [Test]
    public async Task The_funnel_pins_the_filter_bar_open_on_every_browse_tab()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (browse, _) = Browse();
            vm.ActiveTab.Browse = browse;
            await Assert.That(browse.IsFilterBarVisible).IsFalse();

            vm.ShowFilterBar = true;

            await Assert.That(browse.IsFilterBarVisible).IsTrue();
            await Assert.That(browse.IsFiltering).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task Row_details_need_no_setting()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            SeedEditableRow(vm);

            await Assert.That(vm.ToggleRowDetailsCommand.CanExecute(null)).IsTrue();
            Ui.Press(window, CommandId.RowDetails);
            await Assert.That(vm.IsRowDetailOpen).IsTrue();

            window.Close();
        });
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
