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
    [Arguments("SELECT * FROM t WHERE a = 1 |", "AND", "GROUP BY")]
    [Arguments("SELECT * FROM t WHERE a = 'x' |", "OR", "ORDER BY")]
    [Arguments("SELECT * FROM t WHERE a IS NULL |", "AND", "LIMIT")]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.id |", "JOIN", "WHERE")]
    [Arguments("SELECT * FROM t GROUP BY a |", "HAVING", "ORDER BY")]
    [Arguments("SELECT * FROM t ORDER BY a |", "DESC", "NULLS LAST")]
    [Arguments("SELECT * FROM t ORDER BY a DESC |", "LIMIT", "NULLS FIRST")]
    [Arguments("SELECT * FROM t LIMIT 10 |", "OFFSET", "FOR UPDATE")]
    [Arguments("SELECT a, b |", "FROM", "AS")]
    [Arguments("SELECT count(*) |", "FROM", "AS")]
    [Arguments("SELECT * |", "FROM", "INTO")]
    [Arguments("UPDATE t SET a = 1 |", "WHERE", "RETURNING")]
    [Arguments("DELETE FROM t WHERE a = 1 |", "RETURNING", "AND")]
    [Arguments("INSERT INTO t (a) VALUES (1) |", "RETURNING", "ON CONFLICT")]
    [Arguments("SELECT * FROM t WHERE a BETWEEN 1 |", "AND", "AND")]
    [Arguments("SELECT * FROM t WHERE a BETWEEN 1 AND 2 |", "AND", "ORDER BY")]
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
    [Arguments("SELECT row_number() OVER (|", "PARTITION BY")]
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

    // --- Package M ---

    [Test]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "ORDER BY")]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "GROUP BY")]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "IS NOT NULL")]
    [Arguments("SELECT * FROM t ORDER BY a |", "NULLS FIRST")]
    [Arguments("SELECT * FROM a JOIN b ON a.id = b.id |", "LEFT JOIN")]
    [Arguments("SELECT * FROM t WHERE a IS |", "DISTINCT FROM")]
    [Arguments("SELECT * FROM t WHERE a IS NOT |", "DISTINCT FROM")]
    [Arguments("SELECT * FROM t LIMIT 1 |", "FOR UPDATE")]
    [Arguments("INSERT INTO t VALUES (1) ON CONFLICT |", "DO NOTHING")]
    [Arguments("INSERT INTO t VALUES (1) ON CONFLICT (id) DO |", "UPDATE SET")]
    [Arguments("|", "INSERT INTO")]
    [Arguments("|", "DELETE FROM")]
    public async Task C02_a_keyword_never_written_alone_comes_with_its_next_words(string marked, string phrase)
    {
        await Assert.That(At(marked).Keywords).Contains(phrase);
    }

    [Test]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "ORDER")]
    [Arguments("SELECT * FROM t WHERE a = 1 |", "GROUP")]
    [Arguments("INSERT INTO t VALUES (1) ON CONFLICT |", "DO")]
    public async Task A_word_that_cannot_stand_alone_is_not_its_own_row(string marked, string alone)
    {
        await Assert.That(At(marked).Keywords).DoesNotContain(alone);
    }

    [Test]
    public async Task The_phrase_comes_before_its_first_word_at_a_statement_start()
    {
        var starts = At("|").Keywords.ToList();

        await Assert.That(starts.IndexOf("INSERT INTO")).IsLessThan(starts.IndexOf("INSERT"));
        await Assert.That(starts[0]).IsEqualTo("SELECT");
    }

    private static SqlKeywordAdvice At(string marked, Func<string, char> callKind)
    {
        var caret = marked.IndexOf('|');
        return SqlKeywordGrammar.At(marked.Remove(caret, 1), caret, callKind);
    }

    private static char Kinds(string name) => name switch
    {
        "row_number" => 'w',
        "count" or "rank" => 'a',
        "lower" => 'f',
        _ => '\0',
    };

    [Test]
    public async Task After_a_window_functions_call_only_over_follows()
    {
        var advice = At("SELECT row_number() |", Kinds);

        await Assert.That(advice.KeywordsOnly).IsTrue();
        await Assert.That(advice.Keywords).IsEquivalentTo(new[] { "OVER" });
    }

    [Test]
    [Arguments("SELECT count(*) |", true)]
    [Arguments("SELECT rank(1) |", true)]
    [Arguments("SELECT my_function(a) |", true)] // unknown: offered, last
    [Arguments("SELECT lower(a) |", false)]
    [Arguments("SELECT (a + b) |", false)] // no call at all
    [Arguments("SELECT * FROM t WHERE a IN (1, 2) |", false)]
    public async Task After_an_aggregates_call_filter_and_over_may_follow(string marked, bool offered)
    {
        var keywords = At(marked, Kinds).Keywords;

        await Assert.That(keywords.Contains("OVER")).IsEqualTo(offered);
        await Assert.That(keywords.Contains("FILTER")).IsEqualTo(offered);
    }

    [Test]
    [Arguments("SELECT CASE |", "WHEN")]
    [Arguments("SELECT CASE WHEN a THEN 1 |", "ELSE")]
    [Arguments("SELECT CASE WHEN a THEN 1 ELSE 2 |", "END")]
    [Arguments("SELECT CASE WHEN a |", "THEN")]
    public async Task Case_takes_its_own_words(string marked, string expected)
    {
        await Assert.That(At(marked).Keywords).Contains(expected);
    }

    [Test]
    [Arguments("SELECT |", true)]
    [Arguments("SELECT count(|", true)]
    [Arguments("SELECT sum(|", false)]
    [Arguments("SELECT * FROM t WHERE |", false)]
    public async Task The_star_is_offered_where_it_can_go(string marked, bool offered)
    {
        await Assert.That(At(marked).Keywords.Contains("*")).IsEqualTo(offered);
    }

    [Test]
    [Arguments("MERGE |", "INTO")]
    [Arguments("MERGE INTO t c |", "USING")]
    [Arguments("MERGE INTO t c USING s x |", "ON")]
    [Arguments("MERGE INTO t USING s ON s.id = t.id |", "WHEN MATCHED")]
    [Arguments("MERGE INTO t USING s ON s.id = t.id |", "WHEN NOT MATCHED")]
    [Arguments("MERGE INTO t USING s ON s.id = t.id WHEN MATCHED |", "THEN")]
    [Arguments("MERGE INTO t USING s ON s.id = t.id WHEN MATCHED THEN |", "UPDATE SET")]
    [Arguments("MERGE INTO t USING s ON s.id = t.id WHEN NOT MATCHED THEN |", "INSERT")]
    public async Task Merge_takes_its_when_clauses(string marked, string expected)
    {
        await Assert.That(At(marked).Keywords).Contains(expected);
    }

    [Test]
    [Arguments("SELECT row_number() OVER (ORDER BY a) |", "AS")]
    [Arguments("SELECT row_number() OVER (ORDER BY a) |", "FROM")]
    [Arguments("SELECT count(*) FILTER (WHERE a) |", "FROM")]
    [Arguments("SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY a) |", "FROM")]
    [Arguments("INSERT INTO t VALUES (1) ON |", "CONFLICT")]
    [Arguments("INSERT INTO t VALUES (1) ON CONFLICT (id) |", "DO UPDATE SET")]
    [Arguments("INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE |", "SET")]
    [Arguments("MERGE INTO t USING s ON s.id = t.id WHEN MATCHED THEN UPDATE |", "SET")]
    public async Task After_a_window_or_a_conflict_clause_the_statement_goes_on(string marked, string expected)
    {
        await Assert.That(At(marked).Keywords).Contains(expected);
    }

    [Test]
    public async Task After_a_not_matched_then_update_is_not_offered()
    {
        await Assert.That(At("MERGE INTO t USING s ON s.id = t.id WHEN NOT MATCHED THEN |").Keywords).DoesNotContain("UPDATE SET");
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
