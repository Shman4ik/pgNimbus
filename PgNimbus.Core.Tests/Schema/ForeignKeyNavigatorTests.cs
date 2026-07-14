using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class ForeignKeyNavigatorTests
{
    private static readonly ForeignKeyInfo OrderToCustomer = new(
        "sales", "orders", ["customer_id"],
        "public", "customers", ["id"]);

    private static readonly ForeignKeyInfo OrderItemToOrder = new(
        "sales", "order_items", ["order_id"],
        "sales", "orders", ["id"]);

    private static readonly ForeignKeyInfo InvoiceToOrder = new(
        "billing", "invoices", ["order_id"],
        "sales", "orders", ["id"]);

    private static readonly ForeignKeyInfo Composite = new(
        "sales", "shipments", ["order_id", "line_no"],
        "sales", "order_items", ["order_id", "line_no"]);

    private static readonly ForeignKeyInfo[] AllKeys = [OrderToCustomer, OrderItemToOrder, InvoiceToOrder, Composite];

    [Test]
    public async Task FindReferencedRow_ResolvesFkColumnToParent()
    {
        var hop = ForeignKeyNavigator.FindReferencedRow("sales", "orders", "customer_id", AllKeys);

        await Assert.That(hop).IsNotNull();
        await Assert.That(hop!.QualifiedTarget).IsEqualTo("public.customers");
        await Assert.That(hop.TargetColumns).IsEquivalentTo(new[] { "id" });
        await Assert.That(hop.SourceColumns).IsEquivalentTo(new[] { "customer_id" });
    }

    [Test]
    public async Task FindReferencedRow_NonFkColumnFindsNothing()
    {
        var hop = ForeignKeyNavigator.FindReferencedRow("sales", "orders", "total", AllKeys);

        await Assert.That(hop).IsNull();
    }

    [Test]
    public async Task FindReferencedRow_CompositeKeyCarriesAllColumnPairs()
    {
        // Pressing either composite column resolves the whole pair set.
        var hop = ForeignKeyNavigator.FindReferencedRow("sales", "shipments", "line_no", AllKeys);

        await Assert.That(hop).IsNotNull();
        await Assert.That(hop!.QualifiedTarget).IsEqualTo("sales.order_items");
        await Assert.That(hop.TargetColumns).IsEquivalentTo(new[] { "order_id", "line_no" });
        await Assert.That(hop.SourceColumns).IsEquivalentTo(new[] { "order_id", "line_no" });
    }

    [Test]
    public async Task FindReferencingTables_ListsEveryChildOfAKeyCell()
    {
        var hops = ForeignKeyNavigator.FindReferencingTables("sales", "orders", "id", AllKeys);

        await Assert.That(hops.Select(h => h.QualifiedTarget))
            .IsEquivalentTo(new[] { "sales.order_items", "billing.invoices" });
        await Assert.That(hops[0].TargetColumns).IsEquivalentTo(new[] { "order_id" });
        await Assert.That(hops[0].SourceColumns).IsEquivalentTo(new[] { "id" });
    }

    [Test]
    public async Task FindReferencingTables_UnreferencedColumnFindsNothing()
    {
        var hops = ForeignKeyNavigator.FindReferencingTables("sales", "orders", "customer_id", AllKeys);

        await Assert.That(hops).IsEmpty();
    }

    [Test]
    public async Task BuildFilter_RendersTypedLiterals()
    {
        var filter = ForeignKeyNavigator.BuildFilter(["id"], [42]);

        await Assert.That(filter).IsEqualTo("id = 42");
    }

    [Test]
    public async Task BuildFilter_QuotesAndEscapesText()
    {
        var filter = ForeignKeyNavigator.BuildFilter(["code"], ["it's"]);

        await Assert.That(filter).IsEqualTo("code = 'it''s'");
    }

    [Test]
    public async Task BuildFilter_CompositeKeyAndJoinsAndQuotesMixedCaseIdentifiers()
    {
        var filter = ForeignKeyNavigator.BuildFilter(["order_id", "LineNo"], [7L, 3]);

        await Assert.That(filter).IsEqualTo("order_id = 7 AND \"LineNo\" = 3");
    }

    [Test]
    public async Task BuildFilter_NullKeyValueMeansNoFilter()
    {
        var filter = ForeignKeyNavigator.BuildFilter(["id"], [null]);

        await Assert.That(filter).IsNull();
    }

    [Test]
    public async Task BuildFilter_MismatchedShapesMeanNoFilter()
    {
        var filter = ForeignKeyNavigator.BuildFilter(["a", "b"], [1]);

        await Assert.That(filter).IsNull();
    }
}
