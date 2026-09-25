using PgNimbus.Core.Text;
using TUnit.Assertions.Enums;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The scope tree of docs/design/sql-editing-experience.md, package F
/// (T13–T19): which block the caret is in, which sources it and the levels
/// around it can name, which CTEs are in reach, and what a derived table or
/// CTE outputs. Every case also names what must <i>not</i> be visible — a
/// neighbouring scope leaking in is exactly the defect this exists to stop.
/// <c>|</c> marks the caret.
/// </summary>
public class SqlScopeModelTests
{
    private static (SqlScopeModel Model, SqlBlock? Block) At(string marked)
    {
        var caret = marked.IndexOf('|');
        var model = SqlScopeModel.Parse(marked.Remove(caret, 1));
        return (model, model.BlockAt(caret));
    }

    // "u,o | x" — each level's labels, innermost first.
    private static string Levels(string marked)
    {
        var (_, block) = At(marked);
        return block is null
            ? "<none>"
            : string.Join(" | ", SqlScopeModel.VisibleSources(block).Select(l => string.Join(",", l.Select(s => s.Label))));
    }

    private static string[] Ctes(string marked)
    {
        var (_, block) = At(marked);
        return block is null ? [] : [.. SqlScopeModel.VisibleCtes(block).Select(c => c.Name)];
    }

    // --- T13: a closed subquery hands the caret back to the outer block ---

    [Test]
    [Arguments("SELECT * FROM public.users u WHERE id IN (SELECT user_id FROM public.orders o) AND |")]
    [Arguments("SELECT * FROM public.users u WHERE EXISTS (SELECT 1 FROM public.orders o WHERE o.user_id = u.id) AND |")]
    public async Task After_a_subquery_only_the_outer_sources_are_visible(string marked)
    {
        await Assert.That(Levels(marked)).IsEqualTo("u");
    }

    [Test]
    public async Task Inside_an_expression_subquery_the_outer_level_is_visible_after_its_own()
    {
        await Assert.That(Levels("SELECT * FROM public.users u WHERE EXISTS (SELECT 1 FROM public.orders o WHERE |)"))
            .IsEqualTo("o | u");
    }

    [Test]
    [Arguments("SELECT * FROM commerce.|")]
    [Arguments("SELECT * FROM public.orders o JOIN commerce.|")]
    public async Task A_dangling_schema_dot_is_a_qualifier_not_a_table(string marked)
    {
        var (_, block) = At(marked);
        var typed = block!.Sources[^1];

        await Assert.That(typed.Schema).IsEqualTo("commerce");
        await Assert.That(typed.Name).IsEqualTo("");
    }

    // --- T14: an inner name hides the outer one only inside ---

    [Test]
    public async Task A_shadowing_alias_is_inner_first_inside_and_gone_outside()
    {
        await Assert.That(Levels("SELECT * FROM public.users x WHERE EXISTS (SELECT 1 FROM public.orders x WHERE x.|)"))
            .IsEqualTo("x | x");
        var (_, inner) = At("SELECT * FROM public.users x WHERE EXISTS (SELECT 1 FROM public.orders x WHERE x.|)");
        await Assert.That(SqlScopeModel.VisibleSources(inner!)[0][0].Name).IsEqualTo("orders");

        var (_, outer) = At("SELECT * FROM public.users x WHERE EXISTS (SELECT 1 FROM public.orders x) AND x.|");
        await Assert.That(SqlScopeModel.VisibleSources(outer!).Single().Single().Name).IsEqualTo("users");
    }

    // --- T15: a derived table's output ---

    [Test]
    public async Task A_derived_table_exposes_its_select_list_names()
    {
        var (_, block) = At("SELECT q.| FROM (SELECT id AS customer_id, name, count(*), 1 + 1 FROM public.users) q");
        var source = block!.Sources.Single();

        await Assert.That(source.Alias).IsEqualTo("q");
        await Assert.That(source.Derived!.Branches[0].Output.Select(o => o.Name))
            .IsEquivalentTo(new string?[] { "customer_id", "name", "count", null }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_column_alias_list_is_read()
    {
        var (_, block) = At("SELECT | FROM (VALUES (1, 'a')) v(num, label)");

        await Assert.That(block!.Sources.Single().ColumnAliases).IsEquivalentTo(new[] { "num", "label" }, CollectionOrdering.Matching);
        await Assert.That(block.Sources.Single().Derived!.Branches[0].Output.Select(o => o.Name ?? "<unnamed>"))
            .IsEquivalentTo(new[] { "column1", "column2" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Output_items_record_stars_with_their_qualifier()
    {
        var (_, block) = At("WITH x AS (SELECT u.*, o.total FROM public.users u JOIN public.orders o ON true) SELECT | FROM x");
        var output = SqlScopeModel.VisibleCtes(block!).Single().Body.Branches[0].Output;

        await Assert.That(output[0]).IsEqualTo(new SqlOutputItem(null, IsStar: true, StarQualifier: "u"));
        await Assert.That(output[1]).IsEqualTo(new SqlOutputItem("total", RefQualifier: "o", RefColumn: "total"));
    }

    // --- T16: CTE visibility ---

    [Test]
    public async Task The_main_query_sees_every_cte()
    {
        await Assert.That(Ctes("WITH a AS (SELECT 1 AS x), b AS (SELECT * FROM a) SELECT * FROM |"))
            .IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_cte_body_sees_only_the_ctes_before_it()
    {
        await Assert.That(Ctes("WITH a AS (SELECT * FROM |), b AS (SELECT 1 AS x) SELECT 1")).IsEmpty();
        await Assert.That(Ctes("WITH a AS (SELECT 1 AS x), b AS (SELECT * FROM |) SELECT 1")).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task A_recursive_cte_body_sees_itself()
    {
        await Assert.That(Ctes("WITH RECURSIVE r AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM | WHERE n < 5) SELECT * FROM r"))
            .IsEquivalentTo(new[] { "r" });
    }

    [Test]
    public async Task A_cte_inside_a_subquery_stays_inside_it()
    {
        await Assert.That(Ctes("SELECT * FROM (WITH inner_c AS (SELECT 1 AS v) SELECT * FROM inner_c) d JOIN |")).IsEmpty();
        await Assert.That(Ctes("SELECT * FROM (WITH inner_c AS (SELECT 1 AS v) SELECT * FROM |) d")).IsEquivalentTo(new[] { "inner_c" });
    }

    // --- T17: DML RETURNING and VALUES as CTE bodies ---

    [Test]
    public async Task A_dml_cte_outputs_its_returning_list()
    {
        var (_, block) = At("WITH x AS (DELETE FROM public.orders o WHERE o.total < 0 RETURNING id, o.user_id AS uid) SELECT | FROM x");
        var body = SqlScopeModel.VisibleCtes(block!).Single().Body.Branches[0];

        await Assert.That(body.Kind).IsEqualTo(SqlBlockKind.Delete);
        await Assert.That(body.Output.Select(o => o.Name ?? "<unnamed>")).IsEquivalentTo(new[] { "id", "uid" }, CollectionOrdering.Matching);
    }

    // --- T18: FROM subquery / LATERAL / correlated subquery ---

    [Test]
    public async Task A_plain_from_subquery_does_not_see_its_siblings()
    {
        await Assert.That(Levels("SELECT * FROM public.users u, (SELECT * FROM public.orders o WHERE |) d")).IsEqualTo("o");
    }

    [Test]
    public async Task A_lateral_subquery_sees_the_items_before_it_only()
    {
        await Assert.That(Levels("SELECT * FROM public.users u, LATERAL (SELECT * FROM public.orders o WHERE o.user_id = |) d, public.orders later"))
            .IsEqualTo("o | u");
    }

    [Test]
    public async Task A_from_subquery_still_sees_the_levels_above_its_block()
    {
        // The derived table inside a scalar subquery can't see "o" (its FROM
        // sibling) but can see "u" (a level up).
        await Assert.That(Levels("SELECT (SELECT 1 FROM public.orders o, (SELECT * FROM audit.users a WHERE |) d) FROM public.users u"))
            .IsEqualTo("a | u");
    }

    // --- T19: set-operation branches ---

    [Test]
    public async Task Union_branches_do_not_share_sources()
    {
        await Assert.That(Levels("SELECT | FROM public.users u UNION SELECT total FROM public.orders o")).IsEqualTo("u");
        await Assert.That(Levels("SELECT name FROM public.users u UNION ALL SELECT | FROM public.orders o")).IsEqualTo("o");
        await Assert.That(Levels("SELECT name FROM public.users u UNION |")).IsEqualTo("");
    }

    [Test]
    public async Task Parenthesized_branches_are_read_from_inside()
    {
        await Assert.That(Levels("(SELECT id FROM public.users u) EXCEPT (SELECT user_id FROM public.orders o WHERE |)")).IsEqualTo("o");
    }

    // --- DML blocks ---

    [Test]
    public async Task Dml_targets_and_their_from_lists_are_sources()
    {
        await Assert.That(Levels("UPDATE public.orders o SET total = 0 FROM public.users u WHERE |")).IsEqualTo("o,u");
        await Assert.That(Levels("DELETE FROM public.orders o USING public.users u WHERE |")).IsEqualTo("o,u");
        await Assert.That(Levels("INSERT INTO public.orders AS o (id) VALUES (1) RETURNING |")).IsEqualTo("o");
    }

    [Test]
    public async Task An_insert_source_query_does_not_see_the_target()
    {
        await Assert.That(Levels("INSERT INTO public.orders (id) SELECT id FROM public.users u WHERE |")).IsEqualTo("u");
    }

    [Test]
    public async Task Join_kinds_and_using_lists_are_recorded()
    {
        var (_, block) = At("SELECT | FROM a CROSS JOIN b NATURAL LEFT JOIN c JOIN d USING (id, \"Key\") LEFT OUTER JOIN e ON true, f");

        await Assert.That(block!.Sources.Select(s => s.Join)).IsEquivalentTo(new[]
        {
            SqlJoinKind.None, SqlJoinKind.Cross, SqlJoinKind.Natural, SqlJoinKind.Inner, SqlJoinKind.Left, SqlJoinKind.Comma,
        }, CollectionOrdering.Matching);
        await Assert.That(block.Sources[3].UsingColumns).IsEquivalentTo(new[] { "id", "Key" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_query_inside_explain_or_create_view_is_read()
    {
        await Assert.That(Levels("EXPLAIN (ANALYZE) SELECT * FROM public.users u WHERE |")).IsEqualTo("u");
        await Assert.That(Levels("CREATE VIEW v AS SELECT * FROM public.users u WHERE |")).IsEqualTo("u");
        await Assert.That(SqlScopeModel.Parse("CREATE TABLE t (id int) WITH (fillfactor = 70)").Root?.Branches.SelectMany(b => b.Sources)).IsEmpty();
        await Assert.That(SqlScopeModel.Parse("SET search_path TO public").Root).IsNull();
    }

    // --- Robustness ---

    [Test]
    public async Task Nesting_past_the_limit_is_unknown_not_guessed()
    {
        var deep = "SELECT * FROM public.users u WHERE " + string.Concat(Enumerable.Repeat("EXISTS (SELECT 1 FROM t WHERE ", 100));
        var model = SqlScopeModel.Parse(deep);

        var block = model.BlockAt(deep.Length, out var unknown);
        await Assert.That(block).IsNull();
        await Assert.That(unknown).IsTrue();
    }

    [Test]
    public async Task Unclosed_and_half_typed_input_still_reads()
    {
        await Assert.That(Levels("SELECT * FROM public.users u WHERE EXISTS (SELECT 1 FROM |")).IsEqualTo(" | u");
        await Assert.That(Levels("SELECT * FROM (SELECT |")).IsEqualTo("");
        await Assert.That(Levels("WITH x AS (|")).IsEqualTo("");
    }

    [Test]
    public async Task Any_text_at_all_parses_without_throwing()
    {
        const string alphabet = "SELECT FROM WHERE WITH UNION JOIN LATERAL ( ) , . * \"q\" 'x' AS ON USING RETURNING VALUES a b ;";
        var words = alphabet.Split(' ');
        var random = new Random(1234);
        var blocksRead = 0;
        for (var n = 0; n < 2000; n++)
        {
            var text = string.Join(' ', Enumerable.Range(0, random.Next(1, 30)).Select(_ => words[random.Next(words.Length)]));
            var model = SqlScopeModel.Parse(text);
            for (var caret = 0; caret <= text.Length; caret += 3)
            {
                if (model.BlockAt(caret) is { } block)
                {
                    _ = SqlScopeModel.VisibleSources(block);
                    _ = SqlScopeModel.VisibleCtes(block);
                    blocksRead++;
                }
            }
        }

        await Assert.That(blocksRead).IsGreaterThan(0);
    }

    // --- Package G: the clause positions a block records ---

    [Test]
    [Arguments("SELECT a AS x FROM t WHERE |", "where")]
    [Arguments("SELECT a AS x FROM t ORDER BY |", "order")]
    [Arguments("SELECT a AS x FROM t GROUP BY a HAVING |", "having")]
    [Arguments("UPDATE t SET a = 1 WHERE b = 2 RETURNING |", "returning")]
    [Arguments("INSERT INTO t (a) VALUES (1) ON CONFLICT (a) DO UPDATE SET |", "set")]
    public async Task The_clause_at_the_caret_is_the_last_top_level_keyword(string marked, string clause)
    {
        var (_, block) = At(marked);

        await Assert.That(block!.ClauseAt(marked.IndexOf('|'))).IsEqualTo(clause);
    }

    [Test]
    [Arguments("UPDATE t SET |", true)]
    [Arguments("UPDATE t SET a = |", false)]
    [Arguments("UPDATE t SET a = f(1, 2), |", true)]
    [Arguments("UPDATE t SET a = f(1, |", false)]
    [Arguments("UPDATE t SET (a, |) = (1, 2)", true)]
    [Arguments("UPDATE t SET a = 1 WHERE |", false)]
    [Arguments("INSERT INTO t VALUES (1) ON CONFLICT (a) DO UPDATE SET b = excluded.b, c|", true)]
    public async Task Assignment_targets_are_told_from_values(string marked, bool target)
    {
        var caret = marked.IndexOf('|');
        var sql = marked.Remove(caret, 1);
        var block = SqlScopeModel.Parse(sql).BlockAt(caret)!;

        await Assert.That(SqlScopeModel.IsAssignmentTarget(sql, block, caret)).IsEqualTo(target);
    }

    [Test]
    public async Task Insert_column_list_conflict_target_and_excluded_are_located()
    {
        const string sql = "INSERT INTO t AS x (a, b) VALUES (1, 2) ON CONFLICT (a) DO UPDATE SET b = 3 RETURNING a";
        var block = SqlScopeModel.Parse(sql).BlockAt(sql.Length)!;

        await Assert.That(sql[block.InsertColumns!.Value.Start..block.InsertColumns.Value.End]).IsEqualTo("a, b");
        await Assert.That(sql[block.ConflictTarget!.Value.Start..block.ConflictTarget.Value.End]).IsEqualTo("a");
        await Assert.That(block.Target!.Label).IsEqualTo("x");
        await Assert.That(block.SeesExcluded(sql.IndexOf("SET", StringComparison.Ordinal))).IsTrue();
        await Assert.That(block.SeesExcluded(sql.IndexOf("VALUES", StringComparison.Ordinal))).IsFalse();
        await Assert.That(block.SeesExcluded(sql.Length)).IsFalse();
    }

    [Test]
    public async Task A_using_list_records_where_it_is()
    {
        const string sql = "SELECT * FROM a JOIN b USING (id, k) JOIN c ON true";
        var block = SqlScopeModel.Parse(sql).BlockAt(0)!;
        var span = block.Sources[1].UsingSpan!.Value;

        await Assert.That(sql[span.Start..span.End]).IsEqualTo("id, k");
        await Assert.That(block.Sources[2].UsingSpan).IsNull();
    }
}
