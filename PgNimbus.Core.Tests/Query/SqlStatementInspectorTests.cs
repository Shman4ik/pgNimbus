using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Lexical write-detection used to note that an EXPLAIN ANALYZE was rolled back, plus
/// the EXPLAIN recognition/unwrapping the Explain command and the plan-view detection ride on.
/// </summary>
public class SqlStatementInspectorTests
{
    [Test]
    [Arguments("INSERT INTO t VALUES (1)")]
    [Arguments("update t set n = 1")]
    [Arguments("DELETE FROM t WHERE id = 1")]
    [Arguments("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE")]
    [Arguments("  \n  delete from t")]
    [Arguments("-- a comment\nUPDATE t SET n = 1")]
    [Arguments("/* block */ INSERT INTO t DEFAULT VALUES")]
    [Arguments("WITH moved AS (DELETE FROM src RETURNING *) INSERT INTO dst SELECT * FROM moved")]
    public async Task WritesAreDetected(string sql)
    {
        await Assert.That(SqlStatementInspector.IsDataModifying(sql)).IsTrue();
    }

    [Test]
    [Arguments("SELECT * FROM t")]
    [Arguments("  select 1")]
    [Arguments("-- delete everything?\nSELECT * FROM t")]
    [Arguments("SELECT updated_at FROM t ORDER BY updated_at")]
    [Arguments("WITH recent AS (SELECT * FROM t) SELECT * FROM recent")]
    [Arguments("TABLE t")]
    [Arguments("VALUES (1), (2)")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ReadsAreNotDetected(string sql)
    {
        await Assert.That(SqlStatementInspector.IsDataModifying(sql)).IsFalse();
    }

    [Test]
    [Arguments("SELECT * FROM orders")]
    [Arguments("  select 1")]
    [Arguments("-- browse\nSELECT * FROM commerce.orders LIMIT 100")]
    [Arguments("WITH recent AS (SELECT * FROM t) SELECT * FROM recent")]
    [Arguments("TABLE t")]
    [Arguments("VALUES (1), (2)")]
    [Arguments("SHOW search_path")]
    [Arguments("SELECT count(*) FROM t")]
    // A trailing semicolon is one statement, not two.
    [Arguments("SELECT 1;")]
    [Arguments("SELECT 1;  -- done\n")]
    public async Task ReExecutableStatementsAreAllowed(string sql)
    {
        await Assert.That(SqlStatementInspector.IsSafeToReExecute(sql)).IsTrue();
    }

    [Test]
    // Writes, obviously.
    [Arguments("INSERT INTO t VALUES (1) RETURNING *")]
    [Arguments("UPDATE t SET n = 1 RETURNING *")]
    [Arguments("DELETE FROM t RETURNING *")]
    [Arguments("WITH gone AS (DELETE FROM t RETURNING *) SELECT * FROM gone")]
    // Not a result-producing read at all — re-running has no upside and may write.
    [Arguments("CREATE TABLE t (id int)")]
    [Arguments("CALL do_work()")]
    [Arguments("REFRESH MATERIALIZED VIEW mv")]
    // SELECT … INTO creates a table.
    [Arguments("SELECT * INTO backup FROM t")]
    // Side effects hiding inside a read.
    [Arguments("SELECT nextval('s')")]
    [Arguments("SELECT setval('s', 1)")]
    [Arguments("SELECT pg_advisory_lock(42)")]
    [Arguments("SELECT pg_terminate_backend(pid) FROM pg_stat_activity")]
    [Arguments("SELECT * FROM dblink_exec('conn', 'DELETE FROM t')")]
    // The simple query protocol would run both statements, twice.
    [Arguments("SELECT 1; DROP TABLE t")]
    [Arguments("")]
    public async Task NonReExecutableStatementsAreRefused(string sql)
    {
        await Assert.That(SqlStatementInspector.IsSafeToReExecute(sql)).IsFalse();
    }

    [Test]
    [Arguments("EXPLAIN SELECT 1")]
    [Arguments("explain (analyze) select 1")]
    [Arguments("  -- plan it\n  EXPLAIN ANALYZE SELECT 1")]
    public async Task ExplainIsRecognized(string sql)
    {
        await Assert.That(SqlStatementInspector.IsExplain(sql)).IsTrue();
    }

    [Test]
    [Arguments("SELECT * FROM explain_log")]
    [Arguments("explaining")]
    [Arguments("")]
    public async Task NonExplainIsNotRecognized(string sql)
    {
        await Assert.That(SqlStatementInspector.IsExplain(sql)).IsFalse();
    }

    [Test]
    // Parenthesized option list, in every spacing/casing shape.
    [Arguments("EXPLAIN (ANALYZE, BUFFERS) SELECT * FROM t", "SELECT * FROM t")]
    [Arguments("explain(format json)select 1", "select 1")]
    [Arguments("EXPLAIN (FORMAT 'json') SELECT ')' AS t", "SELECT ')' AS t")]
    // Legacy bare-keyword form: ANALYZE and VERBOSE, either or both.
    [Arguments("EXPLAIN SELECT 1", "SELECT 1")]
    [Arguments("EXPLAIN ANALYZE SELECT 1", "SELECT 1")]
    [Arguments("explain verbose select 1", "select 1")]
    [Arguments("EXPLAIN ANALYZE VERBOSE UPDATE t SET n = 1", "UPDATE t SET n = 1")]
    // A statement that only looks like an option keyword stays put.
    [Arguments("EXPLAIN SELECT analyze FROM t", "SELECT analyze FROM t")]
    public async Task ExplainPrefixIsStripped(string sql, string expected)
    {
        await Assert.That(SqlStatementInspector.StripExplain(sql)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SELECT 1")]              // not an EXPLAIN at all
    [Arguments("EXPLAIN")]               // nothing left to hand back
    [Arguments("EXPLAIN (ANALYZE)")]
    public async Task NonWrappingStatementsAreReturnedUnchanged(string sql)
    {
        await Assert.That(SqlStatementInspector.StripExplain(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task StrippedExplainAnalyzeExposesTheWriteUnderneath()
    {
        // What the plan views key on: `EXPLAIN ANALYZE INSERT …` really writes, and
        // the write only becomes visible after the EXPLAIN prefix comes off.
        var sql = "EXPLAIN (ANALYZE) INSERT INTO t VALUES (1)";

        await Assert.That(SqlStatementInspector.IsDataModifying(sql)).IsFalse();
        await Assert.That(SqlStatementInspector.IsDataModifying(SqlStatementInspector.StripExplain(sql))).IsTrue();
    }
}
