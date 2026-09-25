using PgNimbus.Core.Query;
using TUnit.Assertions.Enums;

namespace PgNimbus.Core.Tests.Query;

public class StagedRowCheckTests
{
    private static RowSnapshot Snapshot(params (string Column, object? Value)[] cells) =>
        new(cells.Select(c => c.Column).ToList(), cells.Select(c => c.Value).ToList());

    // --- CellValueComparer ------------------------------------------------------

    [Test]
    public async Task NullsCompareAsValuesNotAsUnknowns()
    {
        await Assert.That(CellValueComparer.Compare(null, null)).IsEqualTo(CellComparison.Equal);
        await Assert.That(CellValueComparer.Compare(null, "x")).IsEqualTo(CellComparison.Different);
        await Assert.That(CellValueComparer.Compare(0, null)).IsEqualTo(CellComparison.Different);
    }

    [Test]
    public async Task ArraysAndByteaCompareByContentNotReference()
    {
        await Assert.That(CellValueComparer.Compare(new[] { 1, 2 }, new[] { 1, 2 })).IsEqualTo(CellComparison.Equal);
        await Assert.That(CellValueComparer.Compare(new[] { 1, 2 }, new[] { 2, 1 })).IsEqualTo(CellComparison.Different);
        await Assert.That(CellValueComparer.Compare(new byte[] { 0xde, 0xad }, new byte[] { 0xde, 0xad })).IsEqualTo(CellComparison.Equal);
        await Assert.That(CellValueComparer.Compare(new string?[] { "a", null }, new string?[] { "a", null })).IsEqualTo(CellComparison.Equal);
        await Assert.That(CellValueComparer.Compare(new[,] { { 1, 2 }, { 3, 4 } }, new[,] { { 1, 2 }, { 3, 4 } })).IsEqualTo(CellComparison.Equal);
        await Assert.That(CellValueComparer.Compare(new[,] { { 1, 2 }, { 3, 4 } }, new[] { 1, 2, 3, 4 })).IsEqualTo(CellComparison.Different);
    }

    [Test]
    public async Task UnreadablePlaceholdersAndMixedWireFormatsAreNotComparable()
    {
        var placeholder = QueryEngine.UnreadableCell("public.address");

        await Assert.That(CellValueComparer.Compare(placeholder, placeholder)).IsEqualTo(CellComparison.Incomparable);
        await Assert.That(CellValueComparer.Compare(placeholder, "(1,2)")).IsEqualTo(CellComparison.Incomparable);
        await Assert.That(CellValueComparer.Compare("1011", new System.Collections.BitArray(4))).IsEqualTo(CellComparison.Incomparable);
    }

    // --- Evaluate ------------------------------------------------------------------

    private static StagedRowCheck CheckFor(PendingChangeSet set) => set.BuildRowCheck()!;

    [Test]
    public async Task UnchangedRowsPass()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "shipped", original: Snapshot(("id", 1), ("status", "packed"), ("note", null)));

        var conflicts = CheckFor(set).Evaluate(["id", "status", "note"], [[1, "packed", null]]);

        await Assert.That(conflicts).IsEmpty();
    }

    [Test]
    public async Task AChangeToAnyLoadedColumnIsAConflictNotJustEditedOnes()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "shipped", original: Snapshot(("id", 1), ("status", "packed"), ("note", null)));

        var conflicts = CheckFor(set).Evaluate(["id", "status", "note"], [[1, "packed", "rush"]]);

        await Assert.That(conflicts).Count().IsEqualTo(1);
        var conflict = conflicts[0];
        await Assert.That(conflict.Kind).IsEqualTo(RowConflictKind.Changed);
        await Assert.That(conflict.StagedAs).IsEqualTo(StagedRowKind.Edit);

        var note = conflict.Columns.Single(c => c.Column == "note");
        await Assert.That(note.ChangedElsewhere).IsTrue();
        await Assert.That(note.Before).IsNull();
        await Assert.That(note.Current).IsEqualTo("rush");
        await Assert.That(note.HasProposed).IsFalse();

        var status = conflict.Columns.Single(c => c.Column == "status");
        await Assert.That(status.ChangedElsewhere).IsFalse();
        await Assert.That(status.HasProposed).IsTrue();
        await Assert.That(status.Proposed).IsEqualTo("shipped");

        await Assert.That(conflict.Describe()).Contains("note was NULL, now 'rush'");
        await Assert.That(conflict.Describe()).Contains("you staged status = 'shipped'");
    }

    [Test]
    public async Task AValueSetToNullElsewhereIsAConflict()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageDelete([1], Snapshot(("id", 1), ("note", "rush")));

        var conflicts = CheckFor(set).Evaluate(["id", "note"], [[1, null]]);

        await Assert.That(conflicts).Count().IsEqualTo(1);
        await Assert.That(conflicts[0].StagedAs).IsEqualTo(StagedRowKind.Delete);
        await Assert.That(conflicts[0].Describe()).Contains("note was 'rush', now NULL");
    }

    [Test]
    public async Task AMissingRowIsReportedAsDeleted()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "shipped", original: Snapshot(("id", 1), ("status", "packed")));
        set.StageEdit([2], "status", "shipped", original: Snapshot(("id", 2), ("status", "packed")));

        var conflicts = CheckFor(set).Evaluate(["id", "status"], [[2, "packed"]]);

        await Assert.That(conflicts).Count().IsEqualTo(1);
        await Assert.That(conflicts[0].Kind).IsEqualTo(RowConflictKind.Deleted);
        await Assert.That(conflicts[0].KeyText).IsEqualTo("id = 1");
        await Assert.That(conflicts[0].Current).IsNull();
        await Assert.That(conflicts[0].Describe()).Contains("no longer exists");
    }

    [Test]
    public async Task CompositeKeysMatchOnEveryPart()
    {
        var set = new PendingChangeSet("sales", "order_lines", ["order_id", "line"]);
        set.StageEdit([7, 1], "qty", 5, original: Snapshot(("order_id", 7), ("line", 1), ("qty", 2)));
        set.StageEdit([7, 2], "qty", 9, original: Snapshot(("order_id", 7), ("line", 2), ("qty", 3)));

        // (7,2) changed; (7,1) is fine; (8,1) is noise that shares a part of a key.
        var conflicts = CheckFor(set).Evaluate(
            ["order_id", "line", "qty"],
            [[7, 1, 2], [7, 2, 4], [8, 1, 3]]);

        await Assert.That(conflicts).Count().IsEqualTo(1);
        await Assert.That(conflicts[0].KeyText).IsEqualTo("order_id = 7, line = 2");
        await Assert.That(conflicts[0].Columns.Single(c => c.Column == "qty").Current).IsEqualTo(4);
    }

    [Test]
    public async Task UnreadableColumnsAreSkippedNotReportedAsConflicts()
    {
        var placeholder = QueryEngine.UnreadableCell("public.address");
        var set = new PendingChangeSet("public", "customers", ["id"]);
        set.StageEdit([1], "name", "Ann", original: Snapshot(("id", 1), ("name", "Anne"), ("ship_to", placeholder)));

        var conflicts = CheckFor(set).Evaluate(["id", "name", "ship_to"], [[1, "Anne", "(\"1 Oak St\",Milan)"]]);

        await Assert.That(conflicts).IsEmpty();
        await Assert.That(set.UncheckedColumns).IsEquivalentTo(new[] { "ship_to" });
    }

    [Test]
    public async Task RowsStagedWithoutASnapshotAreStillCheckedForExistence()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageDelete([1]);

        await Assert.That(CheckFor(set).Evaluate(["id"], [[1]])).IsEmpty();
        await Assert.That(CheckFor(set).Evaluate(["id"], [])).Count().IsEqualTo(1);
    }

    [Test]
    public async Task EditsSupersededByADeleteAreCheckedAsTheDelete()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "shipped", original: Snapshot(("id", 1), ("status", "packed")));
        set.StageDelete([1], Snapshot(("id", 1), ("status", "shipped")));

        var check = CheckFor(set);

        await Assert.That(check.Rows).Count().IsEqualTo(1);
        await Assert.That(check.Rows[0].Kind).IsEqualTo(StagedRowKind.Delete);
        // The first snapshot wins: the second one already showed the staged value.
        await Assert.That(check.Rows[0].Original!.Values[1]).IsEqualTo("packed");
    }

    // --- The lock statement -----------------------------------------------------------

    [Test]
    public async Task LockStatementSelectsKeysThenSnapshotColumnsForUpdate()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "x", original: Snapshot(("id", 1), ("status", "a"), ("note", null)));
        set.StageDelete([2], Snapshot(("id", 2), ("status", "b")));

        var check = CheckFor(set);

        await Assert.That(check.SelectedColumns).IsEquivalentTo(new[] { "id", "status", "note" }, CollectionOrdering.Matching);
        await Assert.That(check.LockStatements).Count().IsEqualTo(1);
        await Assert.That(check.LockStatements[0].Sql).IsEqualTo(
            """SELECT "id", "status", "note" FROM "public"."orders" WHERE "id" IN (@k0_0, @k1_0) ORDER BY "id" FOR UPDATE""");
        await Assert.That(check.LockStatements[0].Parameters["k1_0"]).IsEqualTo(2);
    }

    [Test]
    public async Task CompositeKeyLockStatementUsesRowValuesAndKeyCasts()
    {
        var set = new PendingChangeSet("public", "stock", ["warehouse", "sku"], ["region", null]);
        set.StageDelete(["north", 10]);

        var sql = CheckFor(set).LockStatements[0].Sql;

        await Assert.That(sql).IsEqualTo(
            """SELECT "warehouse", "sku" FROM "public"."stock" WHERE ("warehouse", "sku") IN ((CAST(@k0_0 AS region), @k0_1)) ORDER BY "warehouse", "sku" FOR UPDATE""");
    }

    [Test]
    public async Task LargeSetsAreChunked()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        for (var i = 0; i < 5; i++)
        {
            set.StageDelete([i]);
        }

        await Assert.That(set.BuildRowCheck(chunkSize: 2)!.LockStatements).Count().IsEqualTo(3);
    }

    [Test]
    public async Task InsertOnlySetsNeedNoCheck()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageInsert([]);

        await Assert.That(set.BuildRowCheck()).IsNull();
    }

    // --- Getting out of a conflict -------------------------------------------------

    [Test]
    public async Task RebaseKeepsStagedValuesOnChangedRowsAndDropsDeletedOnes()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "shipped", original: Snapshot(("id", 1), ("status", "packed"), ("note", null)));
        set.StageDelete([2], Snapshot(("id", 2), ("status", "packed"), ("note", null)));

        var conflicts = CheckFor(set).Evaluate(["id", "status", "note"], [[1, "packed", "rush"]]);
        var (rebased, dropped) = set.Rebase(conflicts);

        await Assert.That(rebased).IsEqualTo(1);
        await Assert.That(dropped).IsEqualTo(1);
        await Assert.That(set.IsRowDeleted([2])).IsFalse();
        await Assert.That(set.GetRowEdits([1])!.Single().Value).IsEqualTo("shipped");
        await Assert.That(set.GetOriginal([1])!.Values[2]).IsEqualTo("rush");

        // The same server state now passes.
        await Assert.That(CheckFor(set).Evaluate(["id", "status", "note"], [[1, "packed", "rush"]])).IsEmpty();
    }

    [Test]
    public async Task UnstageDropsOnlyTheConflictingRows()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "shipped", original: Snapshot(("id", 1), ("status", "packed")));
        set.StageEdit([2], "status", "shipped", original: Snapshot(("id", 2), ("status", "packed")));

        var conflicts = CheckFor(set).Evaluate(["id", "status"], [[1, "packed"], [2, "cancelled"]]);
        set.Unstage(conflicts);

        await Assert.That(set.Count).IsEqualTo(1);
        await Assert.That(set.IsRowEdited([1])).IsTrue();
        await Assert.That(set.IsRowEdited([2])).IsFalse();
        await Assert.That(set.GetOriginal([2])).IsNull();
    }

    [Test]
    public async Task ExceptionSummarySaysNothingWasApplied()
    {
        var set = new PendingChangeSet("public", "orders", ["id"]);
        set.StageEdit([1], "status", "shipped", original: Snapshot(("id", 1), ("status", "packed")));
        set.StageDelete([2], Snapshot(("id", 2), ("status", "packed")));

        var conflicts = CheckFor(set).Evaluate(["id", "status"], [[1, "cancelled"]]);
        var ex = new StagedChangesConflictException(conflicts);

        await Assert.That(ex.Message).StartsWith("Commit rolled back — nothing was applied. 1 row was changed and 1 row was deleted");
        await Assert.That(ex.Conflicts).Count().IsEqualTo(2);
    }
}
