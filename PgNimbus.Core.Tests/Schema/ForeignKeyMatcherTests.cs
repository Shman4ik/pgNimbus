using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class ForeignKeyMatcherTests
{
    private static readonly ForeignKeyInfo OrderToCustomer = new(
        "sales", "orders", ["customer_id"],
        "public", "customers", ["id"]);

    private static readonly ForeignKeyInfo OrderItemToOrder = new(
        "sales", "order_items", ["order_id"],
        "sales", "orders", ["id"]);

    private static readonly ForeignKeyInfo Composite = new(
        "sales", "shipments", ["order_id", "line_no"],
        "sales", "order_items", ["order_id", "line_no"]);

    [Test]
    public async Task BuildJoinCondition_UsesAliasesWhenPresent()
    {
        TableReference[] tables =
        [
            new("sales", "orders", "o"),
            new("public", "customers", "c"),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [OrderToCustomer]);

        await Assert.That(condition).IsEqualTo("o.customer_id = c.id");
    }

    [Test]
    public async Task BuildJoinCondition_FallsBackToTableNameWithoutAlias()
    {
        TableReference[] tables =
        [
            new("sales", "orders", null),
            new("public", "customers", null),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [OrderToCustomer]);

        await Assert.That(condition).IsEqualTo("orders.customer_id = customers.id");
    }

    [Test]
    public async Task BuildJoinCondition_WorksRegardlessOfWhichSideIsChild()
    {
        // customers joined first, orders (the FK-holding/child side) second —
        // the condition must still put customer_id on the orders side.
        TableReference[] tables =
        [
            new("public", "customers", "c"),
            new("sales", "orders", "o"),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [OrderToCustomer]);

        await Assert.That(condition).IsEqualTo("o.customer_id = c.id");
    }

    [Test]
    public async Task BuildJoinCondition_JoinsCompositeKeyColumnsWithAnd()
    {
        TableReference[] tables =
        [
            new("sales", "order_items", "oi"),
            new("sales", "shipments", "s"),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [Composite]);

        await Assert.That(condition).IsEqualTo("s.order_id = oi.order_id AND s.line_no = oi.line_no");
    }

    [Test]
    public async Task BuildJoinCondition_SearchesEarlierTablesWhenTheImmediatelyPrecedingOneHasNoFk()
    {
        // products has no FK to order_items; orders (further back) does, via order_items -> orders.
        TableReference[] tables =
        [
            new("sales", "orders", "o"),
            new("public", "products", "p"),
            new("sales", "order_items", "oi"),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [OrderToCustomer, OrderItemToOrder]);

        await Assert.That(condition).IsEqualTo("oi.order_id = o.id");
    }

    [Test]
    public async Task BuildJoinCondition_ReturnsNullWhenNoForeignKeyConnectsAnyPair()
    {
        TableReference[] tables =
        [
            new("public", "products", "p"),
            new("public", "customers", "c"),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [OrderToCustomer, OrderItemToOrder]);

        await Assert.That(condition).IsNull();
    }

    [Test]
    public async Task BuildJoinCondition_ReturnsNullWithFewerThanTwoTables()
    {
        TableReference[] tables = [new("sales", "orders", "o")];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [OrderToCustomer]);

        await Assert.That(condition).IsNull();
    }

    [Test]
    public async Task BuildJoinCondition_QuotesAliasesThatNeedIt()
    {
        // Aliases are stored unquoted (SqlCompletionContext.Unquote strips the
        // quotes a "MyOrders"-style alias was written with) — the mixed case
        // means the generated condition must re-quote it to round-trip.
        TableReference[] tables =
        [
            new("sales", "orders", "MyOrders"),
            new("public", "customers", "c"),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(tables, [OrderToCustomer]);

        await Assert.That(condition).IsEqualTo("\"MyOrders\".customer_id = c.id");
    }

    [Test]
    public async Task BuildJoinCondition_QuotesFallbackTableNameThatNeedsIt()
    {
        // No alias, and the bare table name itself needs quoting (mixed case).
        TableReference[] tables =
        [
            new("sales", "Orders", null),
            new("public", "customers", "c"),
        ];

        var condition = ForeignKeyMatcher.BuildJoinCondition(
            tables, [new ForeignKeyInfo("sales", "Orders", ["customer_id"], "public", "customers", ["id"])]);

        await Assert.That(condition).IsEqualTo("\"Orders\".customer_id = c.id");
    }

    [Test]
    public async Task FindJoinCandidates_IncludesBothTheParentAndChildSide()
    {
        // orders is in the statement; customers (parent) and order_items (child
        // of orders) should both surface as join candidates.
        TableReference[] tables = [new("sales", "orders", "o")];

        var candidates = ForeignKeyMatcher.FindJoinCandidates(tables, [OrderToCustomer, OrderItemToOrder]);

        await Assert.That(candidates).Contains(("public", "customers"));
        await Assert.That(candidates).Contains(("sales", "order_items"));
    }

    [Test]
    public async Task FindJoinCandidates_ExcludesTablesAlreadyInTheStatement()
    {
        TableReference[] tables =
        [
            new("sales", "orders", "o"),
            new("public", "customers", "c"),
        ];

        var candidates = ForeignKeyMatcher.FindJoinCandidates(tables, [OrderToCustomer]);

        await Assert.That(candidates).IsEmpty();
    }

    [Test]
    public async Task FindJoinCandidates_DoesNotDuplicateATableReachableFromTwoStatementTables()
    {
        // Both orders and order_items point at each other transitively; customers
        // is only reachable via orders, and must appear exactly once.
        TableReference[] tables =
        [
            new("sales", "orders", "o"),
            new("sales", "order_items", "oi"),
        ];

        var candidates = ForeignKeyMatcher.FindJoinCandidates(tables, [OrderToCustomer, OrderItemToOrder]);

        await Assert.That(candidates.Count(c => c == ("public", "customers"))).IsEqualTo(1);
    }

    [Test]
    public async Task FindJoinCandidates_ReturnsEmptyWhenNoForeignKeyTouchesAnyStatementTable()
    {
        TableReference[] tables = [new("public", "products", "p")];

        var candidates = ForeignKeyMatcher.FindJoinCandidates(tables, [OrderToCustomer, OrderItemToOrder]);

        await Assert.That(candidates).IsEmpty();
    }

    [Test]
    public async Task FindJoinCandidates_DoesNotExcludeASameNamedTableInADifferentSchema()
    {
        // The statement already has public.orders; line_items (a different table)
        // has its own FK to archive.orders — a distinct table that merely shares a
        // bare name with one already referenced. A bare-name-only "already
        // referenced" check would wrongly hide it; schema+table must both match.
        var lineItemToArchiveOrder = new ForeignKeyInfo(
            "sales", "line_items", ["order_id"], "archive", "orders", ["id"]);
        TableReference[] tables =
        [
            new("public", "orders", "o"),
            new("sales", "line_items", "li"),
        ];

        var candidates = ForeignKeyMatcher.FindJoinCandidates(tables, [lineItemToArchiveOrder]);

        await Assert.That(candidates).Contains(("archive", "orders"));
    }
}
