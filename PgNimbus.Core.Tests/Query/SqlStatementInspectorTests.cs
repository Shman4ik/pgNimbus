using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Lexical write-detection used to note that an EXPLAIN ANALYZE was rolled back.
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
}
