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
}
