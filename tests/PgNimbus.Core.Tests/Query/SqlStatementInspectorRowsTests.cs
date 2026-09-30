using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Which statements the engine may run without describing them first when it
/// would not retry them anyway (a script's second statement onwards, or one
/// inside a transaction). "True" must only ever mean "cannot return rows".
/// </summary>
public class SqlStatementInspectorRowsTests
{
    [Test]
    [Arguments("INSERT INTO t VALUES (1)")]
    [Arguments("update t set n = 1 where id = 2")]
    [Arguments("DELETE FROM t")]
    [Arguments("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE")]
    [Arguments("CREATE TABLE t (id int)")]
    [Arguments("create table t2 as select * from t")]
    [Arguments("ALTER TABLE t ADD COLUMN n int")]
    [Arguments("DROP TABLE t")]
    [Arguments("TRUNCATE t")]
    [Arguments("GRANT SELECT ON t TO app")]
    [Arguments("COMMENT ON TABLE t IS 'a table that returning rows'")]
    [Arguments("SET search_path = app")]
    [Arguments("BEGIN")]
    [Arguments("commit")]
    [Arguments("VACUUM ANALYZE t")]
    [Arguments("DO $$ BEGIN INSERT INTO t VALUES (1) RETURNING id INTO x; END $$")]
    [Arguments("  -- seed\n  INSERT INTO t VALUES (1)")]
    [Arguments("/* returning */ INSERT INTO t VALUES (1)")]
    [Arguments("INSERT INTO t (\"returning\") VALUES (1)")]
    [Arguments("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS 'SELECT 1'")]
    public async Task Statements_that_cannot_return_rows(string sql)
    {
        await Assert.That(SqlStatementInspector.CannotReturnRows(sql)).IsTrue();
    }

    [Test]
    [Arguments("SELECT 1")]
    [Arguments("WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x")]
    [Arguments("WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x")]
    [Arguments("VALUES (1), (2)")]
    [Arguments("TABLE t")]
    [Arguments("SHOW search_path")]
    [Arguments("EXPLAIN INSERT INTO t VALUES (1)")]
    [Arguments("FETCH ALL FROM c")]
    [Arguments("CALL p(1, NULL)")]
    [Arguments("EXECUTE s(1)")]
    [Arguments("COPY t TO STDOUT")]
    [Arguments("INSERT INTO t VALUES (1) RETURNING id")]
    [Arguments("update t set n = 1 returning *")]
    [Arguments("DELETE FROM t\nRETURNING t.*")]
    [Arguments("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE RETURNING *")]
    [Arguments("(SELECT 1)")]
    [Arguments("")]
    [Arguments("  -- only a comment")]
    [Arguments("frobnicate everything")]
    public async Task Statements_that_may(string sql)
    {
        await Assert.That(SqlStatementInspector.CannotReturnRows(sql)).IsFalse();
    }
}
