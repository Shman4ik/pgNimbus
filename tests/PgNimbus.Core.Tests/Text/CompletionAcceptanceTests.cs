using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The Enter rule of docs/dev/design/sql-completion-audit-2.md §6.1 and the
/// new-name positions it leans on (findings A01–A07).
/// </summary>
public class CompletionAcceptanceTests
{
    private static (string Sql, int Caret) AtCaret(string marked)
    {
        var caret = marked.IndexOf('|');
        return (marked.Remove(caret, 1), caret);
    }

    // The filter starts at the word under the caret, as the popup's does.
    private static bool Enter(string marked, CompletionRow row, bool chosen = false)
    {
        var (sql, caret) = AtCaret(marked);
        return CompletionAcceptance.EnterAccepts(sql, caret, CompletionEdits.TokenAt(sql, caret).FilterStart, row, chosen);
    }

    private static CompletionRow Keyword(string name) => new(name, name, IsKeyword: true);

    private static CompletionRow Name(string name, string? insert = null) => new(name, insert ?? name, IsKeyword: false);

    // --- A01: a row that is already written changes nothing ---

    [Test]
    [Arguments("JOIN customers c ON c.id = o.customer_id|", "customer_id")]
    [Arguments("SELECT * FROM t ORDER BY sold DESC|", "DESC")]
    [Arguments("SELECT * FROM t WHERE a IS NULL|", "NULL")]
    [Arguments("SELECT * FROM t WHERE a IS null|", "NULL")]
    public async Task A_row_already_written_in_full_is_not_taken_even_when_chosen(string marked, string name)
    {
        var row = name == name.ToUpperInvariant() ? Keyword(name) : Name(name);

        await Assert.That(Enter(marked, row)).IsFalse();
        await Assert.That(Enter(marked, row, chosen: true)).IsFalse();
    }

    [Test]
    public async Task A_keyword_differing_only_in_case_is_no_change()
    {
        // A07: "= true⏎" must not become "= TRUE".
        await Assert.That(Enter("SELECT * FROM t WHERE c.is_active = true|", Keyword("TRUE"))).IsFalse();
    }

    [Test]
    public async Task A_name_typed_in_full_is_not_qualified_by_enter()
    {
        // A04: "UPDATE customers⏎" must not become "UPDATE commerce.customers".
        var other = Name("customers", "commerce.customers");

        await Assert.That(Enter("UPDATE customers|", other)).IsFalse();
        // Chosen with the arrows, the other schema's table is what was asked for.
        await Assert.That(Enter("UPDATE customers|", other, chosen: true)).IsTrue();
    }

    [Test]
    public async Task A_callable_typed_in_full_gets_its_parens_only_when_chosen()
    {
        var now = Name("now", "now()");

        await Assert.That(Enter("SELECT now|", now)).IsFalse();
        await Assert.That(Enter("SELECT now|", now, chosen: true)).IsTrue();
    }

    [Test]
    public async Task The_start_of_a_name_is_still_taken()
    {
        await Assert.That(Enter("SELECT * FROM t WHERE a IS NU|", Keyword("NULL"))).IsTrue();
        await Assert.That(Enter("SELECT * FROM t ORDER BY a DE|", Keyword("DESC"))).IsTrue();
        await Assert.That(Enter("SELECT * FROM cust|", Name("customers"))).IsTrue();
    }

    [Test]
    public async Task A_loose_fuzzy_match_is_not_taken_unless_chosen()
    {
        await Assert.That(Enter("SELECT * FROM t WHERE ct|", Name("created_at"))).IsFalse();
        await Assert.That(Enter("SELECT * FROM t WHERE ct|", Name("created_at"), chosen: true)).IsTrue();
    }


    // --- A02, A03, A06: new-name positions take only a chosen row ---

    [Test]
    [Arguments("SELECT * FROM customers c|", "CROSS")]
    [Arguments("SELECT * FROM orders o|", "ORDER")]
    [Arguments("SELECT * FROM saas.issues i|", "INNER")]
    [Arguments("SELECT * FROM saas.users u|", "UNION")]
    [Arguments("SELECT * FROM a JOIN org.employees e|", "EXCEPT")]
    [Arguments("SELECT count(*) AS n|", "notifications")]
    [Arguments("SELECT e.name, m.name AS manager|", "manager_id")]
    [Arguments("SELECT * FROM public.customers AS c|", "comments")]
    public async Task At_a_new_name_only_a_chosen_row_is_taken(string marked, string name)
    {
        var row = name == name.ToUpperInvariant() ? Keyword(name) : Name(name);

        await Assert.That(Enter(marked, row)).IsFalse();
        await Assert.That(Enter(marked, row, chosen: true)).IsTrue();
    }

    [Test]
    public async Task After_an_alias_the_next_word_is_a_clause_again()
    {
        await Assert.That(Enter("SELECT * FROM customers c w|", Keyword("WHERE"))).IsTrue();
        await Assert.That(Enter("SELECT * FROM customers c JOIN orders o O|", Keyword("ON"))).IsTrue();
    }

    // --- IsNewNamePosition ---

    [Test]
    [Arguments("SELECT * FROM customers |")]
    [Arguments("SELECT * FROM customers c|")]
    [Arguments("SELECT * FROM public.customers c|")]
    [Arguments("SELECT * FROM \"Order Items\" oi|")]
    [Arguments("SELECT * FROM a, b x|")]
    [Arguments("SELECT * FROM a LEFT JOIN b x|")]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.id JOIN c x|")]
    [Arguments("DELETE FROM order_items oi|")]
    [Arguments("UPDATE customers c|")]
    [Arguments("UPDATE ONLY public.customers c|")]
    [Arguments("MERGE INTO customers c|")]
    [Arguments("SELECT * FROM (SELECT 1) q|")]
    [Arguments("SELECT * FROM generate_series(1, 3) g|")]
    [Arguments("SELECT * FROM a, LATERAL (SELECT 1) l|")]
    [Arguments("SELECT a AS |")]
    [Arguments("SELECT a AS x|")]
    [Arguments("SELECT * FROM t AS x|")]
    [Arguments("WITH |")]
    [Arguments("WITH RECURSIVE r|")]
    [Arguments("WITH a AS (SELECT 1), b|")]
    [Arguments("SELECT * FROM (WITH x|")]
    [Arguments("CREATE TABLE |")]
    [Arguments("CREATE TABLE app.|")]
    [Arguments("CREATE TABLE IF NOT EXISTS t|")]
    [Arguments("CREATE OR REPLACE VIEW v|")]
    [Arguments("CREATE UNIQUE INDEX i|")]
    [Arguments("CREATE MATERIALIZED VIEW mv|")]
    [Arguments("CREATE TEMP SEQUENCE s|")]
    [Arguments("CREATE TABLE t (|")]
    [Arguments("CREATE TABLE t (id int, na|")]
    [Arguments("CREATE TABLE IF NOT EXISTS app.t (id int PRIMARY KEY, |")]
    [Arguments("ALTER TABLE customers ADD COLUMN ph|")]
    [Arguments("ALTER TABLE customers ADD COLUMN IF NOT EXISTS ph|")]
    [Arguments("ALTER TABLE customers RENAME TO cl|")]
    [Arguments("ALTER TABLE customers RENAME COLUMN a TO b|")]
    [Arguments("CREATE VIEW v AS SELECT a AS |")]
    public async Task IsNewNamePosition_true(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        await Assert.That(SqlCompletionContext.IsNewNamePosition(sql, caret)).IsTrue();
    }

    [Test]
    [Arguments("SELECT * FROM |")]
    [Arguments("SELECT * FROM cust|")]
    [Arguments("SELECT * FROM public.|")]
    [Arguments("SELECT * FROM a, |")]
    [Arguments("SELECT * FROM customers c w|")]
    [Arguments("SELECT * FROM customers c |")]
    [Arguments("SELECT * FROM customers AS c |")]
    [Arguments("SELECT * FROM a JOIN b x O|")]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.id |")]
    [Arguments("SELECT * FROM t WHERE a IN (1, 2) |")]
    [Arguments("SELECT * FROM t WHERE a = |")]
    [Arguments("SELECT CAST(a AS |")]
    [Arguments("SELECT CAST(a AS int|")]
    [Arguments("WITH x AS |")]
    [Arguments("WITH x AS (SELECT 1) |")]
    [Arguments("WITH x AS (SELECT 1) SEL|")]
    [Arguments("CREATE VIEW v AS |")]
    [Arguments("CREATE TABLE t AS |")]
    [Arguments("CREATE TABLE t (id |")]
    [Arguments("CREATE TABLE t (id int |")]
    [Arguments("CREATE TABLE t (ts timestamp with |")]
    [Arguments("CREATE TABLE t (id int GENERATED ALWAYS AS |")]
    [Arguments("CREATE INDEX i ON |")]
    [Arguments("CREATE INDEX i ON t (|")]
    [Arguments("CREATE EXTENSION |")]
    [Arguments("ALTER TABLE customers ADD |")]
    [Arguments("ALTER TABLE customers DROP COLUMN |")]
    [Arguments("UPDATE |")]
    [Arguments("UPDATE customers SET |")]
    [Arguments("INSERT INTO customers |")]
    [Arguments("SELECT * FROM t -- FROM customers |")]
    [Arguments("SELECT 'FROM customers |")]
    [Arguments("SELECT a.|")]
    public async Task IsNewNamePosition_false(string marked)
    {
        var (sql, caret) = AtCaret(marked);
        await Assert.That(SqlCompletionContext.IsNewNamePosition(sql, caret)).IsFalse();
    }

    [Test]
    public async Task IsNewNamePosition_reads_only_the_statement_under_the_caret()
    {
        var (sql, caret) = AtCaret("SELECT * FROM customers; SELECT |");
        await Assert.That(SqlCompletionContext.IsNewNamePosition(sql, caret)).IsFalse();
    }
}
