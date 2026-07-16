using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Query;

public class EditableResultDetectorTests
{
    private const uint OrdersOid = 16384;

    private static ColumnInfo Col(string name, uint tableOid = OrdersOid, short attNum = 0) =>
        new(name, "text", typeof(string), tableOid, attNum);

    private static ColumnDetail TableCol(string name, short attNum, bool pk = false) =>
        new(name, "text", NotNull: pk, IsPrimaryKey: pk) { AttNum = attNum };

    // The orders table: id (pk, attnum 1), a dropped column left a gap at 2,
    // status (3), amount (4).
    private static readonly IReadOnlyList<ColumnDetail> Orders =
    [
        TableCol("id", 1, pk: true),
        TableCol("status", 3),
        TableCol("amount", 4),
    ];

    [Test]
    public async Task AllColumnsFromOneTableResolveItsOid()
    {
        var blocker = EditableResultDetector.CheckSingleTable(
            [Col("id", attNum: 1), Col("status", attNum: 3)], out var oid);

        await Assert.That(blocker).IsEqualTo(EditBlocker.None);
        await Assert.That(oid).IsEqualTo(OrdersOid);
    }

    [Test]
    public async Task ExpressionColumnIsComputed()
    {
        // upper(status) has no source table: OID and attnum are both 0.
        var blocker = EditableResultDetector.CheckSingleTable(
            [Col("id", attNum: 1), Col("upper", tableOid: 0, attNum: 0)], out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.ComputedColumns);
    }

    [Test]
    public async Task SystemColumnIsComputed()
    {
        // SELECT ctid, id FROM orders — ctid carries the table OID but a
        // negative attribute number.
        var blocker = EditableResultDetector.CheckSingleTable(
            [Col("ctid", attNum: -1), Col("id", attNum: 1)], out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.ComputedColumns);
    }

    [Test]
    public async Task ColumnsFromTwoTablesAreAJoin()
    {
        var blocker = EditableResultDetector.CheckSingleTable(
            [Col("id", attNum: 1), Col("name", tableOid: OrdersOid + 1, attNum: 1)], out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.MultipleTables);
    }

    [Test]
    public async Task RepeatedColumnIsAmbiguous()
    {
        // SELECT id, id FROM orders — name-keyed commits would be ambiguous.
        var blocker = EditableResultDetector.CheckSingleTable(
            [Col("id", attNum: 1), Col("id", attNum: 1)], out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.RepeatedColumn);
    }

    [Test]
    public async Task FullSelectMatchesPrimaryKey()
    {
        var blocker = EditableResultDetector.MatchPrimaryKey(
            [Col("id", attNum: 1), Col("status", attNum: 3), Col("amount", attNum: 4)],
            Orders,
            out var pk);

        await Assert.That(blocker).IsEqualTo(EditBlocker.None);
        await Assert.That(pk).IsEquivalentTo(["id"]);
    }

    [Test]
    public async Task SubsetWithPrimaryKeyMatches()
    {
        var blocker = EditableResultDetector.MatchPrimaryKey(
            [Col("id", attNum: 1), Col("amount", attNum: 4)],
            Orders,
            out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.None);
    }

    [Test]
    public async Task MissingPrimaryKeyColumnDisqualifies()
    {
        var blocker = EditableResultDetector.MatchPrimaryKey(
            [Col("status", attNum: 3), Col("amount", attNum: 4)],
            Orders,
            out var pk);

        await Assert.That(blocker).IsEqualTo(EditBlocker.PrimaryKeyNotSelected);
        await Assert.That(pk).IsEmpty();
    }

    [Test]
    public async Task AliasedColumnDisqualifies()
    {
        // SELECT id, status AS s FROM orders — the grid header says "s", but
        // every commit path keys SET clauses and PK lookups by displayed name.
        var blocker = EditableResultDetector.MatchPrimaryKey(
            [Col("id", attNum: 1), Col("s", attNum: 3)],
            Orders,
            out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.RenamedColumns);
    }

    [Test]
    public async Task SwappedAliasesDisqualify()
    {
        // SELECT status AS amount, amount AS status FROM orders — names all
        // exist on the table, but each points at the wrong attribute; only the
        // attnum check catches this.
        var blocker = EditableResultDetector.MatchPrimaryKey(
            [Col("id", attNum: 1), Col("amount", attNum: 3), Col("status", attNum: 4)],
            Orders,
            out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.RenamedColumns);
    }

    [Test]
    public async Task TableWithoutPrimaryKeyDisqualifies()
    {
        IReadOnlyList<ColumnDetail> heap = [TableCol("value", 1)];

        var blocker = EditableResultDetector.MatchPrimaryKey([Col("value", attNum: 1)], heap, out _);

        await Assert.That(blocker).IsEqualTo(EditBlocker.NoPrimaryKey);
    }

    [Test]
    public async Task CompositePrimaryKeyRequiresEveryColumn()
    {
        IReadOnlyList<ColumnDetail> orderItems =
        [
            TableCol("order_id", 1, pk: true),
            TableCol("line_no", 2, pk: true),
            TableCol("sku", 3),
        ];

        var full = EditableResultDetector.MatchPrimaryKey(
            [Col("order_id", attNum: 1), Col("line_no", attNum: 2), Col("sku", attNum: 3)],
            orderItems,
            out var fullPk);
        var partial = EditableResultDetector.MatchPrimaryKey(
            [Col("order_id", attNum: 1), Col("sku", attNum: 3)],
            orderItems,
            out _);

        await Assert.That(full).IsEqualTo(EditBlocker.None);
        await Assert.That(fullPk).IsEquivalentTo(["order_id", "line_no"]);
        await Assert.That(partial).IsEqualTo(EditBlocker.PrimaryKeyNotSelected);
    }
}
