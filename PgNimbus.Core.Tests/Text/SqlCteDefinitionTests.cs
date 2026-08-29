using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// Exercises <see cref="SqlCompletionContext.ExtractCteDefinitions"/> — the
/// derivation of a CTE's output columns that lets <c>cte.</c> member access
/// and WHERE-narrowing work over WITH queries.
/// </summary>
public class SqlCteDefinitionTests
{
    private static SqlCompletionContext.CteDefinition Single(string sql)
    {
        var defs = SqlCompletionContext.ExtractCteDefinitions(sql);
        return defs.Count == 1 ? defs[0] : throw new InvalidOperationException($"expected 1 CTE, got {defs.Count}");
    }

    [Test]
    public async Task DeclaredColumnList_IsTheOutputShape()
    {
        var cte = Single("WITH x (a, b) AS (SELECT 1, 2) SELECT * FROM x");

        await Assert.That(cte.Name).IsEqualTo("x");
        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(cte.SelectsStar).IsFalse();
    }

    [Test]
    public async Task SelectList_BareAndDottedReferences()
    {
        var cte = Single("WITH recent AS (SELECT id, o.total FROM orders o) SELECT * FROM recent");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "id", "total" });
        await Assert.That(cte.SelectsStar).IsFalse();
    }

    [Test]
    public async Task SelectList_ExplicitAndImplicitAliases()
    {
        var cte = Single("WITH s AS (SELECT count(*) AS cnt, max(total) top_total FROM orders) SELECT * FROM s");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "cnt", "top_total" });
    }

    [Test]
    public async Task SelectList_UnaliasedExpressionIsSkipped()
    {
        var cte = Single("WITH x AS (SELECT price * qty, id FROM order_items) SELECT * FROM x");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "id" });
    }

    [Test]
    public async Task SelectStar_FlagsStarAndKeepsSourceTables()
    {
        var cte = Single("WITH x AS (SELECT * FROM public.orders) SELECT * FROM x");

        await Assert.That(cte.Columns).IsEmpty();
        await Assert.That(cte.SelectsStar).IsTrue();
        await Assert.That(cte.SourceTables).Count().IsEqualTo(1);
        await Assert.That(cte.SourceTables[0].Table).IsEqualTo("orders");
        await Assert.That(cte.SourceTables[0].Schema).IsEqualTo("public");
    }

    [Test]
    public async Task QualifiedStar_MixesWithNamedColumns()
    {
        var cte = Single(
            "WITH x AS (SELECT o.*, c.name FROM orders o JOIN customers c ON c.id = o.customer_id) SELECT * FROM x");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "name" });
        await Assert.That(cte.SelectsStar).IsTrue();
    }

    [Test]
    public async Task ChainedCtes_EachGetsItsOwnDefinition()
    {
        var defs = SqlCompletionContext.ExtractCteDefinitions(
            "WITH a AS (SELECT id FROM t), b AS (SELECT * FROM a) SELECT * FROM b");

        await Assert.That(defs).Count().IsEqualTo(2);
        await Assert.That(defs[0].Name).IsEqualTo("a");
        await Assert.That(defs[0].Columns).IsEquivalentTo(new[] { "id" });
        await Assert.That(defs[1].Name).IsEqualTo("b");
        await Assert.That(defs[1].SelectsStar).IsTrue();
        await Assert.That(defs[1].SourceTables[0].Table).IsEqualTo("a");
    }

    [Test]
    public async Task RecursiveCte_ColumnsComeFromTheFirstBranch()
    {
        var cte = Single(
            "WITH RECURSIVE tree AS (SELECT id, parent FROM nodes UNION ALL SELECT n.id, n.parent FROM nodes n JOIN tree t ON n.parent = t.id) SELECT * FROM tree");

        await Assert.That(cte.Name).IsEqualTo("tree");
        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "id", "parent" });
    }

    [Test]
    public async Task DistinctOn_QuantifierIsNotAColumn()
    {
        var cte = Single(
            "WITH latest AS (SELECT DISTINCT ON (customer_id) customer_id, total FROM orders ORDER BY customer_id, created_at DESC) SELECT * FROM latest");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "customer_id", "total" });
    }

    [Test]
    public async Task Distinct_WithoutOn()
    {
        var cte = Single("WITH s AS (SELECT DISTINCT status FROM orders) SELECT * FROM s");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "status" });
    }

    [Test]
    public async Task QuotedIdentifiers_Unquote()
    {
        var cte = Single("WITH q AS (SELECT \"Weird Name\", t.\"Other\" FROM t) SELECT * FROM q");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "Weird Name", "Other" });
    }

    [Test]
    public async Task ScalarSubqueryInTheList_DoesNotSplitOrLeakItsFrom()
    {
        var cte = Single("WITH x AS (SELECT id, (SELECT max(v) FROM other) AS peak FROM t) SELECT * FROM x");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "id", "peak" });
    }

    [Test]
    public async Task CaseExpression_OnlyCountsWithAnAlias()
    {
        var without = Single("WITH x AS (SELECT CASE WHEN a THEN 1 ELSE 2 END, b FROM t) SELECT * FROM x");
        await Assert.That(without.Columns).IsEquivalentTo(new[] { "b" });

        var with = Single("WITH x AS (SELECT CASE WHEN a THEN 1 END AS flag FROM t) SELECT * FROM x");
        await Assert.That(with.Columns).IsEquivalentTo(new[] { "flag" });
    }

    [Test]
    public async Task StringLiteralWithAlias_KeepsTheAlias()
    {
        var cte = Single("WITH x AS (SELECT 'fixed' AS label, id FROM t) SELECT * FROM x");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "label", "id" });
    }

    [Test]
    public async Task FunctionWithFromInsideItsParens_DoesNotEndTheList()
    {
        var cte = Single("WITH x AS (SELECT extract(year from created_at) AS yr, id FROM t) SELECT * FROM x");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "yr", "id" });
    }

    [Test]
    public async Task SelectWithoutFrom()
    {
        var cte = Single("WITH one AS (SELECT 1 AS v) SELECT * FROM one");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "v" });
        await Assert.That(cte.SourceTables).IsEmpty();
    }

    [Test]
    public async Task ValuesCte_YieldsNoColumnsWithoutADeclaredList()
    {
        var cte = Single("WITH v AS (VALUES (1, 2)) SELECT * FROM v");

        await Assert.That(cte.Columns).IsEmpty();
        await Assert.That(cte.SelectsStar).IsFalse();
    }

    [Test]
    public async Task UnterminatedBody_StillYieldsWhatIsTypedSoFar()
    {
        // Mid-typing: the WITH body's paren isn't closed yet.
        var cte = Single("WITH recent AS (SELECT id, total FROM orders");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "id", "total" });
        await Assert.That(cte.SourceTables[0].Table).IsEqualTo("orders");
    }

    [Test]
    public async Task NumericLiteralAndBooleanTail_AreNotAliases()
    {
        var cte = Single("WITH x AS (SELECT 1 + 2, active IS NOT NULL, id FROM t) SELECT * FROM x");

        await Assert.That(cte.Columns).IsEquivalentTo(new[] { "id" });
    }
}
