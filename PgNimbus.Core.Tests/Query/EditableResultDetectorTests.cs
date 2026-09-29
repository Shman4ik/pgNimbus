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
    public async Task UnreadableKeyColumnIsAnUnsupportedRowIdentity()
    {
        // An unmapped composite key read without the text fallback: every cell
        // is a placeholder, so no row could be targeted or checked.
        var unreadableId = new ColumnInfo("id", "public.order_ref", typeof(object), OrdersOid, 1);
        var blocker = EditableResultDetector.MatchPrimaryKey(
            [unreadableId, Col("status", attNum: 3)],
            Orders,
            out var pk);

        await Assert.That(blocker).IsEqualTo(EditBlocker.UnreadableKey);
        await Assert.That(pk).IsEmpty();
        await Assert.That(EditableResultDetector.FindUnreadableKey([unreadableId], ["id"])).IsEqualTo(unreadableId);

        // The same key read back as a text literal is fine.
        await Assert.That(EditableResultDetector.FindUnreadableKey([Col("id", attNum: 1)], ["id"])).IsNull();
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
        await Assert.That(fullPk).IsEquivalentTo(["order_id", "line_no"], CollectionOrdering.Matching);
        await Assert.That(partial).IsEqualTo(EditBlocker.PrimaryKeyNotSelected);
    }

    // --- A relation read more than once (security audit 2026-09, finding 14) ---

    [Test]
    public async Task A_self_join_passes_the_metadata_check_so_the_text_has_to_refuse_it()
    {
        // The audit's case: both columns carry items' OID with distinct attnums,
        // and name is the parent's, so an edit of it would update the child.
        const string sql = "SELECT c.id, p.name FROM items c JOIN items p ON p.id = c.parent_id";

        var metadata = EditableResultDetector.CheckSingleTable([Col("id", attNum: 1), Col("name", attNum: 2)], out _);

        await Assert.That(metadata).IsEqualTo(EditBlocker.None);
        await Assert.That(EditableResultDetector.CheckRepeatedTable(sql)).IsEqualTo(EditBlocker.RepeatedTable);
    }

    [Test]
    [Arguments("SELECT c.id, p.name FROM items c, items p WHERE p.id = c.parent_id")]
    [Arguments("SELECT c.id, p.name FROM public.items c JOIN items p ON p.id = c.parent_id;")]
    [Arguments("SELECT c.id, p.name FROM items c JOIN (SELECT * FROM items) p ON p.id = c.parent_id")]
    [Arguments("SELECT c.id, p.name FROM items c CROSS JOIN LATERAL (SELECT * FROM items x WHERE x.id = c.parent_id) p")]
    [Arguments("WITH p AS (SELECT * FROM items) SELECT c.id, p.name FROM items c JOIN p ON p.id = c.parent_id")]
    [Arguments("WITH q AS (SELECT * FROM items) SELECT a.id, b.name FROM q a JOIN q b ON b.id = a.parent_id")]
    [Arguments("SELECT c.id, p.name FROM \"Items\" c JOIN \"Items\" p ON p.id = c.parent_id")]
    public async Task A_relation_read_twice_where_its_columns_can_reach_the_result_is_refused(string sql)
    {
        await Assert.That(EditableResultDetector.CheckRepeatedTable(sql)).IsEqualTo(EditBlocker.RepeatedTable);
    }

    [Test]
    [Arguments("SELECT id, name FROM items")]
    [Arguments("SELECT id, name FROM items;")]
    [Arguments("SELECT o.id, o.status FROM orders o JOIN customers c ON c.id = o.customer_id")]
    [Arguments("SELECT id, name FROM items WHERE parent_id IN (SELECT id FROM items WHERE name = 'root')")]
    [Arguments("SELECT id, name FROM items i WHERE EXISTS (SELECT 1 FROM items p WHERE p.id = i.parent_id)")]
    [Arguments("SELECT c.id, c.name FROM \"Items\" c JOIN items p ON p.id = c.parent_id")]
    [Arguments("CREATE TABLE t (id int)")]
    public async Task One_read_per_relation_is_left_to_the_metadata_checks(string sql)
    {
        // A join of two different tables is still the metadata's call (it
        // reports MultipleTables when both contribute columns), and a subquery
        // inside an expression never contributes a table column.
        await Assert.That(EditableResultDetector.CheckRepeatedTable(sql)).IsEqualTo(EditBlocker.None);
    }

    [Test]
    public async Task Only_rows_every_column_of_which_reads_the_browsed_table_count_as_its_rows()
    {
        const uint otherOid = 16500;

        await Assert.That(EditableResultDetector.ReadsOnlyTable([Col("id", attNum: 1), Col("status", attNum: 3)], OrdersOid)).IsTrue();

        // `FROM orders` in a tab browsing sales.orders, resolved along
        // search_path to public.orders: same names, another OID.
        await Assert.That(EditableResultDetector.ReadsOnlyTable(
            [Col("id", tableOid: otherOid, attNum: 1), Col("status", tableOid: otherOid, attNum: 3)], OrdersOid)).IsFalse();
        await Assert.That(EditableResultDetector.ReadsOnlyTable([Col("id", attNum: 1), Col("upper", tableOid: 0)], OrdersOid)).IsFalse();

        // Nothing to prove it with: no columns, or a browsed table whose OID
        // was never learned.
        await Assert.That(EditableResultDetector.ReadsOnlyTable([], OrdersOid)).IsFalse();
        await Assert.That(EditableResultDetector.ReadsOnlyTable([Col("id", tableOid: 0)], 0)).IsFalse();
    }
}
