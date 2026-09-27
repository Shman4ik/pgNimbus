using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// Which keywords are legal at the caret (sql-completion-audit-2.md §6.2
/// step 1). <c>|</c> marks the caret; the word under it is what the list
/// filters on, never read as context.
/// </summary>
public class SqlKeywordGrammarTests
{
    private static SqlKeywordAdvice At(string marked)
    {
        var caret = marked.IndexOf('|');
        return SqlKeywordGrammar.At(marked.Remove(caret, 1), caret);
    }

    [Test]
    [Arguments("|")]
    [Arguments("sel|")]
    [Arguments("  -- note\n|")]
    public async Task A_statement_start_takes_commands_only(string marked)
    {
        var advice = At(marked);

        await Assert.That(advice.Position).IsEqualTo(SqlKeywordPosition.StatementStart);
        await Assert.That(advice.Keywords).Contains("MERGE");
        await Assert.That(advice.Keywords[0]).IsEqualTo("SELECT");
    }

    [Test]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "AND", "GROUP")]
    [Arguments("SELECT * FROM t WHERE a = 'x' |", "OR", "ORDER")]
    [Arguments("SELECT * FROM t WHERE a IS NULL |", "AND", "LIMIT")]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.id |", "JOIN", "WHERE")]
    [Arguments("SELECT * FROM t GROUP BY a |", "HAVING", "ORDER")]
    [Arguments("SELECT * FROM t ORDER BY a |", "DESC", "NULLS")]
    [Arguments("SELECT * FROM t ORDER BY a DESC |", "LIMIT", "NULLS")]
    [Arguments("SELECT * FROM t LIMIT 10 |", "OFFSET", "FOR")]
    [Arguments("SELECT a, b |", "FROM", "AS")]
    [Arguments("SELECT count(*) |", "FROM", "AS")]
    [Arguments("SELECT * |", "FROM", "INTO")]
    [Arguments("UPDATE t SET a = 1 |", "WHERE", "RETURNING")]
    [Arguments("DELETE FROM t WHERE a = 1 |", "RETURNING", "AND")]
    [Arguments("INSERT INTO t (a) VALUES (1) |", "RETURNING", "ON")]
    [Arguments("SELECT * FROM t WHERE a BETWEEN 1 |", "AND", "AND")]
    [Arguments("SELECT * FROM t WHERE a BETWEEN 1 AND 2 |", "AND", "ORDER")]
    [Arguments("SELECT CASE WHEN a THEN 1 END |", "FROM", "AS")]
    public async Task After_a_finished_expression_only_keywords_follow(string marked, string first, string alsoThere)
    {
        var advice = At(marked);

        await Assert.That(advice.KeywordsOnly).IsTrue();
        await Assert.That(advice.Keywords).Contains(first);
        await Assert.That(advice.Keywords).Contains(alsoThere);
    }

    [Test]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "ON")]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "RETURNING")] // a SELECT's WHERE
    [Arguments("SELECT * FROM t WHERE a = 1 |", "WHEN")]
    [Arguments("SELECT * FROM t ORDER BY a |", "AND")]
    [Arguments("SELECT * FROM t WHERE a BETWEEN 1 |", "OR")]
    [Arguments("SELECT CASE WHEN a THEN 1 END |", "WHEN")]
    public async Task What_cannot_follow_is_left_out(string marked, string absent)
    {
        await Assert.That(At(marked).Keywords).DoesNotContain(absent);
    }

    [Test]
    [Arguments("SELECT * FROM t WHERE a IS |", "NULL")]
    [Arguments("SELECT * FROM t WHERE a IS NOT |", "NULL")]
    [Arguments("SELECT * FROM t WHERE a NOT |", "IN")]
    [Arguments("SELECT * FROM t ORDER |", "BY")]
    [Arguments("SELECT * FROM t GROUP |", "BY")]
    [Arguments("INSERT |", "INTO")]
    [Arguments("DELETE |", "FROM")]
    [Arguments("SELECT 1 UNION |", "SELECT")]
    [Arguments("SELECT * FROM a LEFT |", "JOIN")]
    [Arguments("INSERT INTO t |", "VALUES")]
    [Arguments("INSERT INTO t (a, b) |", "VALUES")]
    [Arguments("UPDATE t |", "SET")]
    [Arguments("WITH x AS (|", "SELECT")]
    [Arguments("WITH x AS (SELECT 1) |", "SELECT")]
    [Arguments("SELECT * FROM t WHERE EXISTS (|", "SELECT")]
    [Arguments("SELECT row_number() OVER (|", "PARTITION")]
    [Arguments("SELECT count(*) FILTER (|", "WHERE")]
    [Arguments("INSERT INTO t VALUES (1) ON CONFLICT DO |", "NOTHING")]
    public async Task Some_positions_take_only_particular_keywords(string marked, string expected)
    {
        var advice = At(marked);

        await Assert.That(advice.KeywordsOnly).IsTrue();
        await Assert.That(advice.Keywords[0]).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SELECT |")]
    [Arguments("SELECT * FROM t WHERE |")]
    [Arguments("SELECT * FROM t WHERE a = 1 AND |")]
    [Arguments("SELECT * FROM t WHERE a = |")]
    [Arguments("SELECT a, |")]
    [Arguments("SELECT count(|")]
    [Arguments("SELECT * FROM t WHERE a IN (|")]
    [Arguments("SELECT * FROM t ORDER BY |")]
    [Arguments("UPDATE t SET a = |")]
    public async Task Where_an_expression_starts_operand_keywords_join_the_names(string marked)
    {
        var advice = At(marked);

        await Assert.That(advice.Position).IsEqualTo(SqlKeywordPosition.Operand);
        await Assert.That(advice.Keywords).Contains("NULL");
        await Assert.That(advice.Keywords).DoesNotContain("ORDER");
        await Assert.That(advice.Keywords).DoesNotContain("IN");
    }

    [Test]
    public async Task After_an_operator_not_and_exists_cannot_start_the_operand()
    {
        await Assert.That(At("SELECT * FROM t WHERE a > |").Keywords).DoesNotContain("NOT");
        await Assert.That(At("SELECT * FROM t WHERE |").Keywords).Contains("NOT");
    }

    [Test]
    [Arguments("SELECT * FROM |")]
    [Arguments("SELECT * FROM a, |")]
    [Arguments("SELECT * FROM a JOIN |")]
    [Arguments("SELECT a AS |")]
    [Arguments("CREATE TABLE t (|")]
    [Arguments("ALTER TABLE t |")]
    [Arguments("SELECT 'text |")]
    [Arguments("SELECT a.|")]
    public async Task Positions_read_elsewhere_get_no_advice(string marked)
    {
        await Assert.That(At(marked).Position).IsEqualTo(SqlKeywordPosition.Unknown);
    }

    [Test]
    [Arguments("SELECT * FROM t WHERE a = 1 AND b = 2 |", false)]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.id AND b.x = |", true)]
    [Arguments("SELECT * FROM a JOIN b ON a.id = |", true)]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.id WHERE |", false)]
    [Arguments("SELECT * FROM a WHERE |", false)]
    public async Task IsInJoinCondition(string marked, bool expected)
    {
        var caret = marked.IndexOf('|');
        await Assert.That(SqlKeywordGrammar.IsInJoinCondition(marked.Remove(caret, 1), caret)).IsEqualTo(expected);
    }
}
