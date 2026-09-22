using PgNimbus.Core.Query;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// What a run tells completion about the catalog (package I, T35): whether it
/// may have changed (so the snapshot is re-read) and whether it moved the
/// session's search_path (so short names stop resolving along the stale one).
/// </summary>
public class SqlStatementInspectorCatalogTests
{
    [Test]
    [Arguments("CREATE TABLE t (id int)", true)]
    [Arguments("  -- note\n alter table t add column x int", true)]
    [Arguments("DROP VIEW v", true)]
    [Arguments("IMPORT FOREIGN SCHEMA s FROM SERVER x INTO public", true)]
    [Arguments("SELECT 1; CREATE INDEX i ON t (id)", true)]
    [Arguments("SELECT * INTO new_table FROM t", true)]
    [Arguments("SELECT * FROM t", false)]
    [Arguments("SELECT 'create table x' FROM t /* drop table y */", false)]
    [Arguments("INSERT INTO t SELECT * FROM s", false)]
    [Arguments("UPDATE t SET a = 1", false)]
    public async Task Catalog_changing_statements_are_recognised(string sql, bool expected)
    {
        await Assert.That(SqlStatementInspector.ChangesCatalog(sql)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SET search_path TO audit, public", true)]
    [Arguments("set local search_path = audit", true)]
    [Arguments("SET SESSION search_path TO x", true)]
    [Arguments("RESET search_path", true)]
    [Arguments("RESET ALL", true)]
    [Arguments("SELECT set_config('search_path', 'audit', true)", true)]
    [Arguments("SET statement_timeout = 0", false)]
    [Arguments("SELECT 'SET search_path TO x'", false)]
    public async Task Search_path_changes_are_recognised(string sql, bool expected)
    {
        await Assert.That(SqlStatementInspector.SetsSearchPath(sql)).IsEqualTo(expected);
    }
}

/// <summary>
/// The popup narrows each keystroke's ranking to the previous keystroke's
/// matches. That is only allowed because it changes nothing: checked here
/// against ranking everything, over random names and queries.
/// </summary>
public class CompletionRankerNarrowingTests
{
    [Test]
    public async Task Narrowing_to_the_previous_matches_ranks_exactly_like_ranking_everything()
    {
        var random = new Random(42);
        const string letters = "abcdeio_";
        var names = Enumerable.Range(0, 400)
            .Select(_ => new string([.. Enumerable.Range(0, random.Next(1, 12)).Select(_ => letters[random.Next(letters.Length)])]))
            .ToList();

        for (var round = 0; round < 200; round++)
        {
            var query = new string([.. Enumerable.Range(0, random.Next(1, 5)).Select(_ => letters[random.Next(letters.Length)])]);
            List<int>? pool = null;
            for (var length = 1; length <= query.Length; length++)
            {
                var prefix = query[..length];
                var narrowed = CompletionRanker.Rank(names, prefix, n => n, n => n.Length % 3, _ => 0, pool, out var matched);
                var full = CompletionRanker.Rank(names, prefix, n => n, n => n.Length % 3, _ => 0);

                await Assert.That(narrowed.Items.SequenceEqual(full.Items)).IsTrue();
                pool = matched;
            }
        }
    }
}
