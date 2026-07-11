using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// Exercises the caret-context heuristics against the query shapes people
/// actually type. The <c>|</c> marker in each SQL sample is the caret.
/// </summary>
public class SqlCompletionContextTests
{
    private static (string Sql, int Caret) AtCaret(string marked)
    {
        var caret = marked.IndexOf('|');
        return (marked.Remove(caret, 1), caret);
    }

    private static SqlClause ClauseAt(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        return SqlCompletionContext.GetCaretContext(sql, caret).Clause;
    }

    private static bool InStringOrCommentAt(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        return SqlCompletionContext.GetCaretContext(sql, caret).InStringOrComment;
    }

    // --- Clause detection over the everyday statement shapes ---

    [Test]
    [Arguments("|", SqlClause.None)]
    [Arguments("SELECT |", SqlClause.ColumnRef)]
    [Arguments("SELECT id, |", SqlClause.ColumnRef)]
    [Arguments("SELECT * FROM |", SqlClause.FromTableRef)]
    [Arguments("SELECT * FROM ord|", SqlClause.FromTableRef)]
    [Arguments("SELECT * FROM orders |", SqlClause.FromTableRef)]
    [Arguments("SELECT * FROM orders, |", SqlClause.FromTableRef)]
    [Arguments("SELECT * FROM orders JOIN |", SqlClause.JoinTableRef)]
    [Arguments("SELECT * FROM orders LEFT JOIN |", SqlClause.JoinTableRef)]
    [Arguments("SELECT * FROM orders o JOIN order_items oi |", SqlClause.JoinTableRef)]
    [Arguments("SELECT * FROM orders WHERE |", SqlClause.Predicate)]
    [Arguments("SELECT * FROM a JOIN b ON |", SqlClause.Predicate)]
    [Arguments("SELECT * FROM a JOIN b USING |", SqlClause.Predicate)]
    [Arguments("SELECT * FROM orders GROUP BY |", SqlClause.Predicate)]
    [Arguments("SELECT * FROM orders ORDER BY |", SqlClause.Predicate)]
    [Arguments("SELECT count(*) FROM orders GROUP BY status HAVING |", SqlClause.Predicate)]
    [Arguments("INSERT INTO |", SqlClause.TableRef)]
    [Arguments("INSERT INTO orders (|", SqlClause.ColumnRef)]
    [Arguments("UPDATE |", SqlClause.TableRef)]
    [Arguments("UPDATE orders SET |", SqlClause.ColumnRef)]
    [Arguments("DELETE FROM |", SqlClause.FromTableRef)]
    [Arguments("TRUNCATE |", SqlClause.TableRef)]
    [Arguments("SELECT CASE WHEN |", SqlClause.ColumnRef)]
    [Arguments("INSERT INTO t (a) VALUES (|", SqlClause.ColumnRef)]
    public async Task GetCaretContext_ClassifiesClause(string marked, SqlClause expected)
    {
        await Assert.That(ClauseAt(marked)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetCaretContext_SemicolonStartsANewStatement()
    {
        await Assert.That(ClauseAt("SELECT * FROM orders; |")).IsEqualTo(SqlClause.None);
        await Assert.That(ClauseAt("SELECT 1; SELECT * FROM |")).IsEqualTo(SqlClause.FromTableRef);
    }

    [Test]
    public async Task GetCaretContext_TheWordBeingTypedIsNotContext()
    {
        // "WHER" in progress must not read as the WHERE keyword.
        await Assert.That(ClauseAt("SELECT * FROM orders WHER|")).IsEqualTo(SqlClause.FromTableRef);
    }

    [Test]
    [Arguments("SELECT 'in a |string' FROM t")]
    [Arguments("SELECT * FROM t -- a comment |")]
    [Arguments("SELECT * /* block | comment */ FROM t")]
    [Arguments("SELECT * /* nested /* still | inside */ */ FROM t")]
    [Arguments("SELECT $$dollar | quoted$$")]
    [Arguments("SELECT 'unterminated |")]
    public async Task GetCaretContext_SuppressesInsideStringsAndComments(string marked)
    {
        await Assert.That(InStringOrCommentAt(marked)).IsTrue();
    }

    [Test]
    public async Task GetCaretContext_AfterAClosedLiteral_IsNotSuppressed()
    {
        await Assert.That(InStringOrCommentAt("SELECT 'done' |")).IsFalse();
        await Assert.That(InStringOrCommentAt("SELECT * /* done */ FROM |")).IsFalse();
    }

    // --- IsAfterCompleteJoinTarget: the ON/USING boost gate ---

    [Test]
    [Arguments("SELECT * FROM customers c JOIN public.orders o |")]
    [Arguments("SELECT * FROM customers c JOIN orders |")]
    [Arguments("SELECT * FROM customers c JOIN orders AS o |")]
    [Arguments("SELECT * FROM customers c LEFT JOIN orders o |")]
    [Arguments("SELECT * FROM customers c LEFT OUTER JOIN orders o |")]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.a_id JOIN public.orders o |")]
    [Arguments("SELECT * FROM t JOIN \"Order Items\" oi |")]
    [Arguments("SELECT * FROM t JOIN orders o\n|")]
    // A "join" inside a comment or string earlier in the statement must not
    // pose as the JOIN whose target is being checked.
    [Arguments("SELECT * FROM t /* join note */ JOIN orders o |")]
    [Arguments("SELECT 'self join', * FROM t JOIN orders o |")]
    public async Task IsAfterCompleteJoinTarget_TrueAfterFinishedTableAndAlias(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        await Assert.That(SqlCompletionContext.IsAfterCompleteJoinTarget(sql, caret)).IsTrue();
    }

    [Test]
    // No JOIN in sight at all.
    [Arguments("SELECT * FROM orders |")]
    // Nothing after JOIN yet — table position, not ON position.
    [Arguments("SELECT * FROM customers c JOIN |")]
    // Table or alias still being typed: no trailing space, and "o" could as
    // easily become an alias as the first letter of ON.
    [Arguments("SELECT * FROM customers c JOIN public.orders|")]
    [Arguments("SELECT * FROM customers c JOIN public.orders o|")]
    // AS typed, alias itself still coming.
    [Arguments("SELECT * FROM customers c JOIN orders AS |")]
    // The user is typing the next JOIN's flavour, not an alias.
    [Arguments("SELECT * FROM customers c JOIN orders inner |")]
    // Past the target entirely: ON already typed, this gate no longer applies.
    [Arguments("SELECT * FROM a JOIN b ON |")]
    public async Task IsAfterCompleteJoinTarget_FalseWhileTargetIsIncomplete(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        await Assert.That(SqlCompletionContext.IsAfterCompleteJoinTarget(sql, caret)).IsFalse();
    }

    // --- IsAfterOnKeyword: the FK join-condition trigger ---

    [Test]
    public async Task IsAfterOnKeyword_TrueRightAfterOn_IncludingMidWord()
    {
        var (sql, caret) = AtCaret("SELECT * FROM a JOIN b ON |");
        await Assert.That(SqlCompletionContext.IsAfterOnKeyword(sql, caret)).IsTrue();

        // The word in progress is the filter, not context — still "after ON".
        (sql, caret) = AtCaret("SELECT * FROM a JOIN b ON a|");
        await Assert.That(SqlCompletionContext.IsAfterOnKeyword(sql, caret)).IsTrue();
    }

    [Test]
    [Arguments("SELECT * FROM a JOIN b ON a.id = |")]
    [Arguments("SELECT * FROM a WHERE |")]
    [Arguments("SELECT * FROM season |")]
    public async Task IsAfterOnKeyword_FalseElsewhere(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        await Assert.That(SqlCompletionContext.IsAfterOnKeyword(sql, caret)).IsFalse();
    }

    // --- ExtractTables over the common statement shapes ---

    [Test]
    public async Task ExtractTables_SimpleFrom()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT * FROM orders");

        await Assert.That(tables).HasCount().EqualTo(1);
        await Assert.That(tables[0]).IsEqualTo(new SqlCompletionContext.TableRef("", "orders", null));
    }

    [Test]
    public async Task ExtractTables_SchemaQualifiedWithAlias()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT * FROM public.orders o");

        await Assert.That(tables[0]).IsEqualTo(new SqlCompletionContext.TableRef("public", "orders", "o"));
    }

    [Test]
    public async Task ExtractTables_JoinChainWithAliases()
    {
        var tables = SqlCompletionContext.ExtractTables(
            "SELECT * FROM customers c JOIN orders o ON c.id = o.customer_id LEFT JOIN order_items oi ON o.id = oi.order_id");

        await Assert.That(tables).HasCount().EqualTo(3);
        await Assert.That(tables[0].Table).IsEqualTo("customers");
        await Assert.That(tables[0].Alias).IsEqualTo("c");
        await Assert.That(tables[1].Table).IsEqualTo("orders");
        await Assert.That(tables[1].Alias).IsEqualTo("o");
        await Assert.That(tables[2].Table).IsEqualTo("order_items");
        await Assert.That(tables[2].Alias).IsEqualTo("oi");
    }

    [Test]
    public async Task ExtractTables_CommaList()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT * FROM customers c, orders o WHERE c.id = o.customer_id");

        await Assert.That(tables).HasCount().EqualTo(2);
        await Assert.That(tables[0].Alias).IsEqualTo("c");
        await Assert.That(tables[1].Alias).IsEqualTo("o");
    }

    [Test]
    public async Task ExtractTables_AsAlias()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT * FROM orders AS o");

        await Assert.That(tables[0].Alias).IsEqualTo("o");
    }

    [Test]
    public async Task ExtractTables_TrailingKeywordIsNotAnAlias()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT * FROM orders WHERE id = 1");

        await Assert.That(tables[0]).IsEqualTo(new SqlCompletionContext.TableRef("", "orders", null));
    }

    [Test]
    public async Task ExtractTables_UpdateAndInsertTargets()
    {
        var update = SqlCompletionContext.ExtractTables("UPDATE public.orders SET status = 'done'");
        await Assert.That(update[0]).IsEqualTo(new SqlCompletionContext.TableRef("public", "orders", null));

        var insert = SqlCompletionContext.ExtractTables("INSERT INTO orders (id, status) VALUES (1, 'new')");
        await Assert.That(insert[0].Table).IsEqualTo("orders");
    }

    [Test]
    public async Task ExtractTables_QuotedIdentifiers()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT * FROM \"Sales\".\"Order Items\" oi");

        await Assert.That(tables[0]).IsEqualTo(new SqlCompletionContext.TableRef("Sales", "Order Items", "oi"));
    }

    [Test]
    public async Task ExtractTables_KeywordInsideACommentDoesNotCutTheFromBody()
    {
        // "order" in the line comment must not end the FROM span before the JOIN.
        var tables = SqlCompletionContext.ExtractTables(
            "SELECT * FROM customers c -- order by signup date\nJOIN orders o ON c.id = o.customer_id");

        await Assert.That(tables).HasCount().EqualTo(2);
        await Assert.That(tables[1].Table).IsEqualTo("orders");
    }

    [Test]
    public async Task ExtractTables_FromInsideAStringLiteralIsNotAFromClause()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT 'copied from fake_table' AS note");

        await Assert.That(tables).IsEmpty();
    }

    // --- ExtractCteNames ---

    [Test]
    public async Task ExtractCteNames_SingleAndChained()
    {
        var single = SqlCompletionContext.ExtractCteNames("WITH recent AS (SELECT 1) SELECT * FROM recent");
        await Assert.That(single).IsEquivalentTo(new[] { "recent" });

        var chained = SqlCompletionContext.ExtractCteNames(
            "WITH a AS (SELECT 1), b AS (SELECT 2) SELECT * FROM a JOIN b ON true");
        await Assert.That(chained).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task ExtractCteNames_Recursive()
    {
        var names = SqlCompletionContext.ExtractCteNames(
            "WITH RECURSIVE tree AS (SELECT id FROM nodes UNION ALL SELECT n.id FROM nodes n JOIN tree t ON n.parent = t.id) SELECT * FROM tree");

        await Assert.That(names).IsEquivalentTo(new[] { "tree" });
    }

    // --- GetQualifierBeforeCaret ---

    [Test]
    public async Task GetQualifierBeforeCaret_AliasAndMidWord()
    {
        var (sql, caret) = AtCaret("SELECT o.| FROM orders o");
        await Assert.That(SqlCompletionContext.GetQualifierBeforeCaret(sql, caret)).IsEqualTo("o");

        (sql, caret) = AtCaret("SELECT o.stat| FROM orders o");
        await Assert.That(SqlCompletionContext.GetQualifierBeforeCaret(sql, caret)).IsEqualTo("o");
    }

    [Test]
    public async Task GetQualifierBeforeCaret_QuotedQualifierUnquotes()
    {
        var (sql, caret) = AtCaret("SELECT \"Order Items\".| FROM \"Order Items\"");
        await Assert.That(SqlCompletionContext.GetQualifierBeforeCaret(sql, caret)).IsEqualTo("Order Items");
    }

    [Test]
    public async Task GetQualifierBeforeCaret_BareIdentifierHasNone()
    {
        var (sql, caret) = AtCaret("SELECT stat| FROM orders");
        await Assert.That(SqlCompletionContext.GetQualifierBeforeCaret(sql, caret)).IsNull();
    }
}
