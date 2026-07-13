using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// Exercises <see cref="SqlCompletionContext.ExpandSelectStar"/> — the
/// "Expand SELECT *" action — against a fake catalog resolver.
/// </summary>
public class SelectStarExpansionTests
{
    // The fake catalog: bare and schema-qualified spellings both resolve,
    // mirroring how the App's resolver answers.
    private static readonly Dictionary<string, string[]> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orders"] = ["id", "customer_id", "total"],
        ["public.orders"] = ["id", "customer_id", "total"],
        ["customers"] = ["id", "name"],
        ["public.customers"] = ["id", "name"],
        ["weird"] = ["Weird Name", "ok"],
        ["source_rows"] = ["a", "b"],
    };

    private static IReadOnlyList<string>? ColumnsFor(string schema, string table)
    {
        var key = schema.Length == 0 ? table : $"{schema}.{table}";
        return Catalog.TryGetValue(key, out var columns) ? columns : null;
    }

    private static string? Apply(string sql, int? caret = null)
    {
        var expansion = SqlCompletionContext.ExpandSelectStar(sql, caret ?? sql.Length, ColumnsFor);
        if (expansion is not { } e)
        {
            return null;
        }

        return sql.Remove(e.Start, e.Length).Insert(e.Start, e.Replacement);
    }

    [Test]
    public async Task SingleTable_ExpandsUnqualified()
    {
        await Assert.That(Apply("SELECT * FROM orders"))
            .IsEqualTo("SELECT id, customer_id, total FROM orders");
    }

    [Test]
    public async Task MultiTable_QualifiesByAliasOrName()
    {
        await Assert.That(Apply("SELECT * FROM orders o JOIN customers ON customers.id = o.customer_id"))
            .IsEqualTo("SELECT o.id, o.customer_id, o.total, customers.id, customers.name FROM orders o JOIN customers ON customers.id = o.customer_id");
    }

    [Test]
    public async Task QualifiedStar_ExpandsJustThatTable_KeepingOtherItems()
    {
        await Assert.That(Apply("SELECT o.*, c.name FROM orders o JOIN customers c ON c.id = o.customer_id"))
            .IsEqualTo("SELECT o.id, o.customer_id, o.total, c.name FROM orders o JOIN customers c ON c.id = o.customer_id");
    }

    [Test]
    public async Task NonStarItems_SurviveVerbatim_IncludingLiterals()
    {
        await Assert.That(Apply("SELECT 'x' AS label, * FROM customers"))
            .IsEqualTo("SELECT 'x' AS label, id, name FROM customers");
    }

    [Test]
    public async Task Distinct_KeepsTheQuantifier()
    {
        await Assert.That(Apply("SELECT DISTINCT * FROM customers"))
            .IsEqualTo("SELECT DISTINCT id, name FROM customers");
    }

    [Test]
    public async Task UnknownTable_ExpandsNothing()
    {
        await Assert.That(Apply("SELECT * FROM mystery_table")).IsNull();
    }

    [Test]
    public async Task UnknownTableInAJoin_ExpandsNothing_AllOrNothing()
    {
        await Assert.That(Apply("SELECT * FROM orders o JOIN mystery m ON m.id = o.id")).IsNull();
    }

    [Test]
    public async Task NoStar_ExpandsNothing()
    {
        await Assert.That(Apply("SELECT id, name FROM customers")).IsNull();
    }

    [Test]
    public async Task CountStar_IsNotASelectListStar()
    {
        await Assert.That(Apply("SELECT count(*) FROM orders")).IsNull();
    }

    [Test]
    public async Task NoFromYet_ExpandsNothing()
    {
        await Assert.That(Apply("SELECT * ")).IsNull();
    }

    [Test]
    public async Task WithCte_OuterStarUsesOuterFromOnly()
    {
        // The resolver knows nothing about "recent", so the CTE body's
        // "orders" must not leak in as an expansion source; the caller (App)
        // resolves CTE names itself — here it can't, so: nothing.
        await Assert.That(Apply("WITH recent AS (SELECT id FROM orders) SELECT * FROM recent")).IsNull();

        // And when the resolver does answer for the CTE name, only its
        // columns appear — not orders'.
        var sql = "WITH recent AS (SELECT id FROM orders) SELECT * FROM source_rows";
        await Assert.That(Apply(sql))
            .IsEqualTo("WITH recent AS (SELECT id FROM orders) SELECT a, b FROM source_rows");
    }

    [Test]
    public async Task InsertSelect_ExpandsTheFromTable_NotTheTarget()
    {
        await Assert.That(Apply("INSERT INTO customers SELECT * FROM source_rows"))
            .IsEqualTo("INSERT INTO customers SELECT a, b FROM source_rows");
    }

    [Test]
    public async Task SecondStatement_ExpandsAtTheCaret()
    {
        var sql = "SELECT 1; SELECT * FROM customers";
        await Assert.That(Apply(sql, sql.Length))
            .IsEqualTo("SELECT 1; SELECT id, name FROM customers");
    }

    [Test]
    public async Task ColumnsNeedingQuotes_GetThem()
    {
        await Assert.That(Apply("SELECT * FROM weird"))
            .IsEqualTo("SELECT \"Weird Name\", ok FROM weird");
    }

    [Test]
    public async Task AliasedTable_QualifiedStarByAlias()
    {
        await Assert.That(Apply("SELECT o.* FROM orders o"))
            .IsEqualTo("SELECT o.id, o.customer_id, o.total FROM orders o");
    }

    [Test]
    public async Task SchemaQualifiedTable_Resolves()
    {
        await Assert.That(Apply("SELECT * FROM public.orders"))
            .IsEqualTo("SELECT id, customer_id, total FROM public.orders");
    }

    [Test]
    public async Task SchemaQualifiedStar_Resolves()
    {
        await Assert.That(Apply("SELECT public.orders.* FROM public.orders"))
            .IsEqualTo("SELECT public.orders.id, public.orders.customer_id, public.orders.total FROM public.orders");
    }

    [Test]
    public async Task SchemaQualifiedStar_DoesNotMatchAnAliasedTable()
    {
        // An alias makes the schema-qualified spelling illegal SQL for this
        // table ("orders o" can only be referenced as o. or orders.), so
        // "public.orders.*" must not resolve through it.
        await Assert.That(Apply("SELECT public.orders.* FROM public.orders o")).IsNull();
    }

    [Test]
    public async Task StarInsideAStringLiteral_IsNotAStar()
    {
        await Assert.That(Apply("SELECT '*' AS star FROM orders")).IsNull();
    }
}
