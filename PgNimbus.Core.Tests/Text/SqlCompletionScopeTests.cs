using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The audit cases from docs/design/sql-editing-experience.md that live in
/// Core: statement boundaries for completion, literal detection by the shared
/// lexer, the clause stack across subqueries, name folding, qualifier chains
/// and the JOIN kinds that take no condition. <c>|</c> marks the caret.
/// </summary>
public class SqlCompletionScopeTests
{
    private static (string Sql, int Caret) AtCaret(string marked)
    {
        var caret = marked.IndexOf('|');
        return (marked.Remove(caret, 1), caret);
    }

    private static SqlCompletionContext.CaretContext ContextAt(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        return SqlCompletionContext.GetCaretContext(sql, caret);
    }

    private static string StatementAt(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        var (start, end) = SqlCompletionContext.CompletionStatementSpan(sql, caret);
        return sql[start..end];
    }

    // --- F01 / T02 / T03: the statement is the unit ---

    [Test]
    public async Task Completion_statement_excludes_neighbours_on_both_sides()
    {
        await Assert.That(StatementAt("SELECT * FROM public.users u;\nSELECT * FROM public.orders o WHERE |"))
            .IsEqualTo("\nSELECT * FROM public.orders o WHERE ");
        await Assert.That(StatementAt("SELECT u.| FROM users u; SELECT * FROM orders"))
            .IsEqualTo("SELECT u. FROM users u");
    }

    [Test]
    public async Task A_caret_after_the_semicolon_starts_a_new_empty_statement()
    {
        await Assert.That(StatementAt("SELECT 1; |")).IsEqualTo(" ");
        await Assert.That(StatementAt("SELECT 1;|")).IsEqualTo("");
    }

    [Test]
    public async Task A_semicolon_inside_a_literal_is_not_a_boundary()
    {
        await Assert.That(StatementAt("SELECT E'a\\';b' FROM t WHERE |")).IsEqualTo("SELECT E'a\\';b' FROM t WHERE ");
    }

    // --- F04 / T11: the caret inside every literal form ---

    [Test]
    [Arguments("SELECT E'can\\'t |';")]
    [Arguments("SELECT $tag1$hello |$tag1$;")]
    [Arguments("SELECT $тег$hello |$тег$;")]
    [Arguments("SELECT /* a /* b */ still | */ 1")]
    [Arguments("SELECT U&'x |'")]
    public async Task Inside_any_literal_form_is_prose(string marked)
    {
        await Assert.That(ContextAt(marked).InStringOrComment).IsTrue();
    }

    [Test]
    public async Task After_a_closed_escape_string_is_code_again()
    {
        var context = ContextAt("SELECT E'can\\'t' FROM |");

        await Assert.That(context.InStringOrComment).IsFalse();
        await Assert.That(context.Clause).IsEqualTo(SqlClause.FromTableRef);
    }

    // --- T10: an open quoted identifier is a name being typed ---

    [Test]
    public async Task An_open_quoted_identifier_is_not_prose()
    {
        var context = ContextAt("SELECT * FROM t WHERE \"na|");

        await Assert.That(context.InStringOrComment).IsFalse();
        await Assert.That(context.InQuotedIdentifier).IsTrue();
        await Assert.That(context.Clause).IsEqualTo(SqlClause.Predicate);
    }

    // --- F03 / T13: a closed subquery hands the clause back ---

    [Test]
    [Arguments("SELECT * FROM public.users\nWHERE id IN (SELECT user_id FROM public.orders) AND |", SqlClause.Predicate)]
    [Arguments("SELECT * FROM t WHERE EXISTS (SELECT 1 FROM o WHERE o.x = t.x) AND |", SqlClause.Predicate)]
    [Arguments("SELECT * FROM (SELECT id FROM users) q |", SqlClause.FromTableRef)]
    [Arguments("SELECT count(*), |", SqlClause.ColumnRef)]
    [Arguments("SELECT * FROM t WHERE id IN (SELECT user_id FROM |", SqlClause.FromTableRef)]
    public async Task The_clause_stack_follows_parentheses(string marked, SqlClause expected)
    {
        await Assert.That(ContextAt(marked).Clause).IsEqualTo(expected);
    }

    // --- F02 / T07 / T08: qualifier chains and folding ---

    [Test]
    public async Task The_whole_qualifier_chain_is_read()
    {
        var (sql, caret) = AtCaret("SELECT public.users.| FROM public.users");
        var chain = SqlCompletionContext.GetQualifierChainBeforeCaret(sql, caret);

        await Assert.That(chain.Select(p => p.Name)).IsEquivalentTo(new[] { "public", "users" });
    }

    [Test]
    public async Task Bare_names_fold_quoted_names_stay_exact()
    {
        var (sql, caret) = AtCaret("SELECT \"Sales\".\"A\"\"b\".x, Public.Users.| FROM t");
        var chain = SqlCompletionContext.GetQualifierChainBeforeCaret(sql, caret);
        await Assert.That(chain.Select(p => p.Name)).IsEquivalentTo(new[] { "public", "users" });

        (sql, caret) = AtCaret("SELECT \"Sales\".\"A\"\"b\".| FROM t");
        chain = SqlCompletionContext.GetQualifierChainBeforeCaret(sql, caret);
        await Assert.That(chain[0]).IsEqualTo(new SqlCompletionContext.NamePart("Sales", true));
        await Assert.That(chain[1]).IsEqualTo(new SqlCompletionContext.NamePart("A\"b", true));
    }

    [Test]
    public async Task A_quoted_member_being_typed_keeps_its_qualifier()
    {
        var (sql, caret) = AtCaret("SELECT u.\"Na| FROM users u");

        await Assert.That(SqlCompletionContext.GetQualifierBeforeCaret(sql, caret)).IsEqualTo("u");
    }

    [Test]
    public async Task Table_refs_carry_the_folded_name()
    {
        var tables = SqlCompletionContext.ExtractTables("SELECT * FROM Public.Users U JOIN \"Audit\".\"Users\" a ON true");

        await Assert.That(tables[0]).IsEqualTo(new SqlCompletionContext.TableRef("public", "users", "u"));
        await Assert.That(tables[1]).IsEqualTo(new SqlCompletionContext.TableRef("Audit", "Users", "a"));
    }

    // --- F09 / T21: joins that take no ON/USING ---

    [Test]
    [Arguments("SELECT * FROM users u CROSS JOIN public.orders |")]
    [Arguments("SELECT * FROM users u NATURAL JOIN public.orders o |")]
    [Arguments("SELECT * FROM users u NATURAL LEFT JOIN public.orders o |")]
    public async Task Cross_and_natural_joins_are_not_waiting_for_a_condition(string marked)
    {
        var (sql, caret) = AtCaret(marked);

        await Assert.That(SqlCompletionContext.IsAfterCompleteJoinTarget(sql, caret)).IsFalse();
    }

    [Test]
    public async Task Using_lists_and_natural_joins_are_read()
    {
        var merged = SqlCompletionContext.ExtractUsingColumns("SELECT * FROM a JOIN b USING (id, \"Key\")", out var natural);

        await Assert.That(merged).IsEquivalentTo(new[] { "id", "Key" });
        await Assert.That(natural).IsFalse();

        SqlCompletionContext.ExtractUsingColumns("SELECT * FROM a NATURAL JOIN b", out natural);
        await Assert.That(natural).IsTrue();
    }

    [Test]
    public async Task After_call_is_detected()
    {
        var (sql, caret) = AtCaret("CALL refr|");

        await Assert.That(SqlCompletionContext.IsAfterKeyword(sql, caret, "call")).IsTrue();
    }
}
