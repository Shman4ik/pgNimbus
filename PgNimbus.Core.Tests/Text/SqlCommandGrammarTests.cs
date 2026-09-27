using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The slot grammar of DDL and utility statements (sql-completion-audit-2.md
/// D01, D02, appendix B scenarios 20–25). <c>|</c> marks the caret; the word
/// under it is what the list filters on, never read as context.
/// </summary>
public class SqlCommandGrammarTests
{
    private static SqlCommandAdvice? At(string marked)
    {
        var caret = marked.IndexOf('|');
        return SqlCommandGrammar.At(marked.Remove(caret, 1), caret);
    }

    [Test]
    [Arguments("CREATE |", "TABLE")] // scenario 20
    [Arguments("CREATE |", "MATERIALIZED VIEW")]
    [Arguments("CREATE |", "UNIQUE INDEX")]
    [Arguments("CREATE |", "OR REPLACE FUNCTION")]
    [Arguments("CREATE OR |", "REPLACE")]
    [Arguments("CREATE UNIQUE |", "INDEX")]
    [Arguments("ALTER |", "TABLE")]
    [Arguments("DROP |", "VIEW")]
    [Arguments("COMMENT |", "ON")]
    [Arguments("COMMENT ON |", "COLUMN")]
    [Arguments("REFRESH |", "MATERIALIZED VIEW")]
    [Arguments("CREATE INDEX i ON public.orders USING btree (a) |", "WHERE")]
    [Arguments("ALTER TABLE public.customers |", "ADD COLUMN")]
    [Arguments("ALTER TABLE public.customers |", "OWNER TO")]
    [Arguments("ALTER TABLE public.customers ALTER COLUMN email |", "SET NOT NULL")]
    [Arguments("ALTER TABLE public.customers RENAME COLUMN email |", "TO")]
    [Arguments("CREATE TABLE t (id int |", "PRIMARY KEY")]
    [Arguments("CREATE TABLE t (id int REFERENCES users (id) |", "ON DELETE")]
    [Arguments("CREATE TABLE t (id int REFERENCES users (id) ON DELETE |", "CASCADE")]
    [Arguments("CREATE TABLE t (id int NOT |", "NULL")]
    [Arguments("CREATE TABLE t (|", "PRIMARY KEY")]
    [Arguments("CREATE FUNCTION f(a int) |", "RETURNS")]
    [Arguments("CREATE EXTENSION hstore |", "SCHEMA")]
    [Arguments("EXPLAIN (|", "ANALYZE")] // scenario 25
    [Arguments("EXPLAIN (ANALYZE, |", "BUFFERS")]
    [Arguments("EXPLAIN (FORMAT |", "JSON")]
    [Arguments("TRUNCATE public.orders |", "RESTART IDENTITY")]
    [Arguments("VACUUM (|", "VERBOSE")]
    [Arguments("LOCK TABLE t IN |", "ACCESS EXCLUSIVE MODE")]
    [Arguments("BEGIN ISOLATION LEVEL |", "SERIALIZABLE")]
    [Arguments("GRANT |", "SELECT")]
    [Arguments("GRANT SELECT |", "ON")]
    [Arguments("GRANT SELECT ON ALL |", "TABLES IN SCHEMA")]
    [Arguments("SET work_mem |", "TO")]
    [Arguments("COPY public.orders |", "FROM")]
    [Arguments("COPY public.orders FROM STDIN WITH (FORMAT |", "CSV")]
    public async Task The_keywords_that_come_next(string marked, string expected)
    {
        await Assert.That(At(marked)!.Keywords).Contains(expected);
    }

    [Test]
    [Arguments("CREATE TABLE t (id |", SqlObjectKind.Type)] // D01: types
    [Arguments("CREATE TABLE t (id int, name |", SqlObjectKind.Type)]
    [Arguments("CREATE TABLE t (id int REFERENCES |", SqlObjectKind.Table)]
    [Arguments("CREATE TABLE t (id int, FOREIGN KEY (id) REFERENCES |", SqlObjectKind.Table)]
    [Arguments("CREATE INDEX ON |", SqlObjectKind.Relation)]
    [Arguments("CREATE INDEX i ON public.orders USING |", SqlObjectKind.IndexMethod)]
    [Arguments("ALTER TABLE |", SqlObjectKind.Table)]
    [Arguments("ALTER TABLE public.customers ADD COLUMN x |", SqlObjectKind.Type)] // scenario 22
    [Arguments("ALTER TABLE public.customers ALTER COLUMN email TYPE |", SqlObjectKind.Type)]
    [Arguments("ALTER TABLE public.customers OWNER TO |", SqlObjectKind.Role)]
    [Arguments("ALTER TABLE public.customers SET SCHEMA |", SqlObjectKind.Schema)]
    [Arguments("DROP VIEW |", SqlObjectKind.View)]
    [Arguments("DROP VIEW IF EXISTS |", SqlObjectKind.View)]
    [Arguments("DROP FUNCTION |", SqlObjectKind.Function)]
    [Arguments("DROP SCHEMA |", SqlObjectKind.Schema)]
    [Arguments("DROP MATERIALIZED VIEW |", SqlObjectKind.MaterializedView)]
    [Arguments("DROP INDEX |", SqlObjectKind.Index)]
    [Arguments("DROP TABLE a, |", SqlObjectKind.Table)]
    [Arguments("COMMENT ON TABLE |", SqlObjectKind.Table)]
    [Arguments("GRANT SELECT ON |", SqlObjectKind.Relation)]
    [Arguments("GRANT SELECT ON SCHEMA |", SqlObjectKind.Schema)]
    [Arguments("GRANT USAGE ON ALL TABLES IN SCHEMA |", SqlObjectKind.Schema)]
    [Arguments("GRANT SELECT ON t TO |", SqlObjectKind.Role)]
    [Arguments("REVOKE SELECT ON t FROM |", SqlObjectKind.Role)]
    [Arguments("TRUNCATE |", SqlObjectKind.Table)]
    [Arguments("VACUUM |", SqlObjectKind.Relation)]
    [Arguments("VACUUM ANALYZE |", SqlObjectKind.Relation)]
    [Arguments("ANALYZE |", SqlObjectKind.Relation)]
    [Arguments("REINDEX TABLE |", SqlObjectKind.Table)]
    [Arguments("CLUSTER t USING |", SqlObjectKind.Index)]
    [Arguments("REFRESH MATERIALIZED VIEW |", SqlObjectKind.MaterializedView)]
    [Arguments("CREATE EXTENSION |", SqlObjectKind.AvailableExtension)]
    [Arguments("DROP EXTENSION |", SqlObjectKind.Extension)]
    [Arguments("SET |", SqlObjectKind.Setting)] // scenario 24, D02
    [Arguments("SHOW |", SqlObjectKind.Setting)]
    [Arguments("RESET |", SqlObjectKind.Setting)]
    [Arguments("SET search_path TO |", SqlObjectKind.SettingValue)]
    [Arguments("SET search_path TO public, |", SqlObjectKind.SettingValue)]
    [Arguments("ALTER SYSTEM SET |", SqlObjectKind.Setting)]
    [Arguments("SET ROLE |", SqlObjectKind.Role)]
    [Arguments("COPY |", SqlObjectKind.Relation)]
    [Arguments("LISTEN |", SqlObjectKind.Channel)]
    [Arguments("CREATE FUNCTION f(a int) RETURNS |", SqlObjectKind.Type)]
    [Arguments("CREATE FUNCTION f(a |", SqlObjectKind.Type)]
    [Arguments("CREATE FUNCTION f(a int) RETURNS int LANGUAGE |", SqlObjectKind.Language)]
    [Arguments("CREATE SCHEMA s AUTHORIZATION |", SqlObjectKind.Role)]
    [Arguments("CREATE TRIGGER t BEFORE INSERT ON |", SqlObjectKind.Table)]
    [Arguments("CREATE DOMAIN d AS |", SqlObjectKind.Type)]
    public async Task The_objects_that_come_next(string marked, SqlObjectKind expected)
    {
        await Assert.That(At(marked)!.Objects).IsEqualTo(expected);
    }

    [Test]
    [Arguments("ALTER TABLE public.customers DROP COLUMN |", "public.customers")] // scenario 21
    [Arguments("ALTER TABLE public.customers DROP |", "public.customers")]
    [Arguments("ALTER TABLE customers ALTER COLUMN |", "customers")]
    [Arguments("ALTER TABLE customers RENAME COLUMN |", "customers")]
    [Arguments("CREATE INDEX idx ON public.orders (|", "public.orders")] // scenario 23
    [Arguments("CREATE INDEX idx ON public.orders (customer_id, |", "public.orders")]
    [Arguments("CREATE INDEX ON orders USING gin (|", "orders")]
    [Arguments("CREATE TABLE t (id int REFERENCES saas.users (|", "saas.users")]
    [Arguments("CREATE TABLE t (id int, FOREIGN KEY (id) REFERENCES saas.users (|", "saas.users")]
    [Arguments("COPY public.orders (|", "public.orders")]
    public async Task A_column_slot_names_its_relation(string marked, string relation)
    {
        var advice = At(marked)!;

        await Assert.That(advice.Objects).IsEqualTo(SqlObjectKind.Column);
        await Assert.That(string.Join('.', advice.Relation!)).IsEqualTo(relation);
    }

    [Test]
    [Arguments("CREATE TABLE |")]
    [Arguments("CREATE TABLE t (|")]
    [Arguments("CREATE TABLE t (id int, |")]
    [Arguments("ALTER TABLE t ADD COLUMN |")]
    [Arguments("ALTER TABLE t RENAME TO |")]
    [Arguments("CREATE INDEX |")]
    [Arguments("CREATE SCHEMA |")]
    public async Task A_name_being_made_is_a_new_name(string marked)
    {
        await Assert.That(At(marked)!.NewName).IsTrue();
    }

    [Test]
    [Arguments("SET statement_timeout TO |", "statement_timeout")]
    [Arguments("SET LOCAL work_mem = |", "work_mem")]
    public async Task A_settings_value_names_the_setting(string marked, string setting)
    {
        await Assert.That(At(marked)!.Setting).IsEqualTo(setting);
    }

    [Test]
    [Arguments("SELECT |")]
    [Arguments("INSERT INTO t |")]
    [Arguments("EXPLAIN SELECT |")]
    [Arguments("ALTER TABLE saas.|")] // member completion's
    [Arguments("CREATE VIEW v AS SELECT |")]
    [Arguments("COMMENT ON TABLE t IS '|")]
    [Arguments("CREATE TABLE t (price numeric(10, |")]
    [Arguments("CREATE TABLE t (id int DEFAULT |")]
    public async Task What_other_grammars_read_or_nobody_can_gets_no_advice(string marked)
    {
        var advice = At(marked);

        await Assert.That(advice is null || (advice.Keywords.Count == 0 && advice.Objects == SqlObjectKind.None && !advice.NewName)).IsTrue();
    }

    [Test]
    public async Task Any_text_at_all_finishes()
    {
        // Runs on the UI thread per keystroke: it must answer (or decline) on anything.
        const string pieces = "create alter drop table index on ( ) , . references grant to set = ' \" if not exists x";
        var words = pieces.Split(' ');
        var random = new Random(3);
        var read = 0;
        for (var n = 0; n < 3000; n++)
        {
            var text = string.Join(' ', Enumerable.Range(0, random.Next(1, 9)).Select(_ => words[random.Next(words.Length)]));
            _ = SqlCommandGrammar.At(text, random.Next(text.Length + 1));
            read++;
        }

        await Assert.That(read).IsEqualTo(3000);
    }
}
