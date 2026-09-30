using PgNimbus.App.Completion;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

namespace PgNimbus.App.Tests;

/// <summary>
/// The completion provider against an in-memory catalog — the audit's fixture
/// (docs/design/sql-editing-experience.md §4): <c>public.users(id, name)</c>,
/// <c>audit.users(id, audit_only)</c>, <c>public.orders(id, user_id, total)</c>.
/// Every case checks what must be <i>absent</i> as well as what must be there:
/// noise from another scope is exactly what a presence-only check misses.
/// <c>|</c> marks the caret.
/// </summary>
public class CompletionProviderTests
{
    private static CompletionCatalog Catalog(IReadOnlyList<string>? searchPath) => new(
        ["audit", "custom", "public"],
        [
            new CompletionTable("public", "users", [Col("users", "id", "int4"), Col("users", "name", "text")]),
            new CompletionTable("audit", "users", [Col("users", "id", "int4"), Col("users", "audit_only", "text")]),
            new CompletionTable("public", "orders", [Col("orders", "id", "int4"), Col("orders", "user_id", "int4"), Col("orders", "total", "numeric")]),
        ],
        [
            new CompletionFunction("custom", new FunctionInfo("MyFunc", "x integer", "integer", 'f')),
            new CompletionFunction("public", new FunctionInfo("normalize", "t text", "text", 'f')),
            new CompletionFunction("custom", new FunctionInfo("normalize", "t text", "text", 'f')),
            new CompletionFunction("public", new FunctionInfo("refresh_all", "", "", 'p')),
        ],
        [
            new ForeignKeyInfo("public", "orders", ["user_id"], "public", "users", ["id"]),
            new ForeignKeyInfo("public", "orders", ["user_id"], "audit", "users", ["id"]),
        ],
        searchPath);

    private static TableColumn Col(string table, string column, string type) => new(table, column, type);

    private static SqlCompletionProvider Provider(IReadOnlyList<string>? searchPath = null, params string[] excluded)
    {
        var provider = new SqlCompletionProvider(null) { ExcludedSchemas = new HashSet<string>(excluded) };
        provider.Load(Catalog(searchPath ?? ["public"]));
        return provider;
    }

    private static IReadOnlyList<SqlCompletionData> At(SqlCompletionProvider provider, string marked)
    {
        var caret = marked.IndexOf('|');
        return provider.GetCompletionData(marked.Remove(caret, 1), caret);
    }

    // What a column row writes, e.g. "name" or "u.id".
    private static string[] ColumnInserts(IEnumerable<SqlCompletionData> items) =>
        [.. items.Where(i => i.Kind == SqlCompletionKind.Column).Select(i => i.InsertText)];

    // --- F01 / T02: the neighbour statement stays out ---

    [Test]
    public async Task A_previous_statement_contributes_nothing()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM public.users u;\nSELECT * FROM public.orders o WHERE |"));

        await Assert.That(columns).Contains("total");
        await Assert.That(columns).DoesNotContain("name");
    }

    [Test]
    public async Task A_following_statement_contributes_nothing()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM public.users u WHERE |;\nSELECT * FROM public.orders"));

        await Assert.That(columns).Contains("name");
        await Assert.That(columns).DoesNotContain("total");
    }

    [Test]
    public async Task T04_A_from_to_the_right_still_names_the_sources()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT u.| FROM public.users u"));

        await Assert.That(columns).IsEquivalentTo(new[] { "id", "name" });
    }

    // --- F02 / T05–T07: names resolve like the server resolves them ---

    [Test]
    public async Task T05_A_short_name_follows_the_search_path()
    {
        var onPublic = ColumnInserts(At(Provider(["public"]), "SELECT * FROM users u WHERE u.|"));
        await Assert.That(onPublic).IsEquivalentTo(new[] { "id", "name" });

        var onAudit = ColumnInserts(At(Provider(["audit", "public"]), "SELECT * FROM users u WHERE u.|"));
        await Assert.That(onAudit).IsEquivalentTo(new[] { "id", "audit_only" });
    }

    [Test]
    public async Task An_unknown_search_path_resolves_only_unique_names()
    {
        var provider = new SqlCompletionProvider(null);
        provider.Load(Catalog(searchPath: null));

        await Assert.That(ColumnInserts(At(provider, "SELECT * FROM users u WHERE u.|"))).IsEmpty();
        await Assert.That(ColumnInserts(At(provider, "SELECT * FROM orders o WHERE o.|"))).Contains("total");
    }

    [Test]
    public async Task T06_A_missing_schema_borrows_nothing()
    {
        await Assert.That(ColumnInserts(At(Provider(), "SELECT * FROM missing.users u WHERE u.|"))).IsEmpty();
    }

    [Test]
    public async Task T07_A_qualified_chain_names_exactly_one_table()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT public.users.| FROM public.users"));

        await Assert.That(columns).IsEquivalentTo(new[] { "id", "name" });
    }

    [Test]
    [Arguments("SELECT * FROM public.|")]
    [Arguments("SELECT * FROM public.us|")]
    [Arguments("SELECT * FROM public.| WHERE true")]
    [Arguments("SELECT * FROM public.orders o JOIN public.|")]
    [Arguments("SELECT * FROM PUBLIC.|")]
    public async Task A_schema_being_typed_as_a_from_item_lists_its_tables(string marked)
    {
        var tables = At(Provider(), marked).Where(i => i.Kind == SqlCompletionKind.Table).Select(i => i.InsertText).ToArray();

        await Assert.That(tables).Contains("users");
        await Assert.That(tables).Contains("orders");
    }

    // What the popup would preselect: the provider's list through the ranker
    // the editor uses, with the word before the caret as the query.
    private static string Preselected(SqlCompletionProvider provider, string marked)
    {
        var caret = marked.IndexOf('|');
        var start = CompletionEdits.TokenAt(marked.Remove(caret, 1), caret).FilterStart;

        var ranked = CompletionRanker.Rank(
            At(provider, marked), marked[start..caret], d => d.Text, d => d.Priority, _ => int.MaxValue);
        return ranked.Items[ranked.SelectedIndex].Text;
    }

    [Test]
    [Arguments("SELECT * FROM public.customers c w|", "WHERE")]
    [Arguments("SELECT * FROM public.users w|", "WHERE")]
    [Arguments("SELECT * FROM public.users u |", "WHERE")]
    [Arguments("SELECT * FROM public.users u j|", "JOIN")]
    [Arguments("SELECT * FROM public.users u o|", "ORDER BY")]
    [Arguments("SELECT * FROM public.users u, public.orders o w|", "WHERE")]
    [Arguments("DELETE FROM public.users u w|", "WHERE")]
    public async Task After_a_finished_from_item_the_next_clause_is_preselected(string marked, string expected)
    {
        await Assert.That(Preselected(Provider(), marked)).IsEqualTo(expected);
    }

    // --- A04 / B03 / F01 (second audit): the search_path decides which relation a bare name is ---

    // The row the popup would preselect, as a whole item.
    private static SqlCompletionData PreselectedItem(SqlCompletionProvider provider, string marked)
    {
        var caret = marked.IndexOf('|');
        var start = CompletionEdits.TokenAt(marked.Remove(caret, 1), caret).FilterStart;

        var ranked = CompletionRanker.Rank(
            At(provider, marked), marked[start..caret], d => d.Text, d => d.Priority, _ => int.MaxValue);
        return ranked.Items[ranked.SelectedIndex];
    }

    [Test]
    [Arguments("SELECT * FROM us|")]
    [Arguments("SELECT * FROM users|")]
    [Arguments("UPDATE users|")]
    [Arguments("DELETE FROM users|")]
    [Arguments("SELECT * FROM public.orders o JOIN users|")]
    public async Task A_bare_name_preselects_the_relation_the_search_path_finds_and_writes_it_bare(string marked)
    {
        var item = PreselectedItem(Provider(["public"]), marked);

        await Assert.That(item.Detail).IsEqualTo("public");
        await Assert.That(item.InsertText).IsEqualTo("users");
    }

    [Test]
    public async Task The_same_name_in_another_schema_is_offered_below_and_qualified()
    {
        var tables = At(Provider(["public"]), "UPDATE users|").Where(i => i.Kind == SqlCompletionKind.Table && i.Text == "users").ToList();

        await Assert.That(tables.Select(t => t.InsertText)).IsEquivalentTo(new[] { "users", "audit.users" });
        await Assert.That(tables.Single(t => t.Detail == "public").Priority)
            .IsGreaterThan(tables.Single(t => t.Detail == "audit").Priority);
    }

    [Test]
    public async Task A_path_that_puts_another_schema_first_moves_the_preference_with_it()
    {
        var item = PreselectedItem(Provider(["audit", "public"]), "UPDATE users|");

        await Assert.That(item.Detail).IsEqualTo("audit");
        await Assert.That(item.InsertText).IsEqualTo("users");
        await Assert.That(At(Provider(["audit", "public"]), "UPDATE users|")
            .Single(i => i.Kind == SqlCompletionKind.Table && i.Text == "users" && i.Detail == "public").InsertText).IsEqualTo("public.users");
    }

    [Test]
    public async Task With_an_unknown_path_only_a_name_one_schema_has_is_written_bare()
    {
        var provider = Provider(["public"]);
        provider.SessionSearchPathChanged = true;
        var tables = At(provider, "SELECT * FROM |").Where(i => i.Kind == SqlCompletionKind.Table).Select(i => i.InsertText).ToList();

        await Assert.That(tables).Contains("orders");
        await Assert.That(tables).Contains("public.users");
        await Assert.That(tables).Contains("audit.users");
        await Assert.That(tables).DoesNotContain("users");
    }


    [Test]
    [Arguments("SELECT * FROM public.users u JOIN public.orders o |", "ON")]
    [Arguments("SELECT * FROM public.users u JOIN public.orders o o|", "ON")]
    [Arguments("SELECT * FROM public.users u JOIN public.orders o us|", "USING")]
    public async Task After_a_finished_join_target_the_condition_still_comes_first(string marked, string expected)
    {
        await Assert.That(Preselected(Provider(), marked)).IsEqualTo(expected);
    }

    // --- Typing replay: the path a person types, not only finished text ---
    //
    // Every other case here puts the caret into a statement that is already
    // complete. The "FROM commerce.|" bug lived for releases because nobody
    // stands where a test put them: it only shows while the statement is being
    // written. So each statement below is replayed word by word: with the text
    // up to a word (and then its first letter) in the editor, that word must
    // be on offer — alone, or as the start of a phrase row (ORDER BY).
    // Aliases, literals and operators are the user's to type.

    public static IEnumerable<string> TypedStatements() =>
    [
        "SELECT u.name FROM public.users u WHERE u.id = 1 ORDER BY u.name",
        "SELECT o.total FROM public.orders o JOIN public.users u ON u.id = o.user_id WHERE o.total > 0",
        "SELECT * FROM audit.users a WHERE a.audit_only IS NOT NULL",
        "SELECT * FROM public.users WHERE name = 'x'",
        "SELECT * FROM users WHERE id = 1",
        "UPDATE public.orders SET total = 0 WHERE id = 1",
        "DELETE FROM public.orders WHERE user_id = 2",
        "SELECT user_id FROM public.orders GROUP BY user_id",
    ];

    private static readonly HashSet<string> UserTyped = new(StringComparer.OrdinalIgnoreCase) { "u", "o", "a", "x" };

    [Test]
    [MethodDataSource(nameof(TypedStatements))]
    public async Task Every_word_of_a_statement_is_offered_while_it_is_typed(string sql)
    {
        var provider = Provider();
        var missing = new List<string>();
        var tokens = SqlLexer.Tokenize(sql);
        for (var t = 0; t < tokens.Count; t++)
        {
            var token = tokens[t];
            var word = sql[token.Start..token.End];
            if (token.Kind != SqlTokenKind.Word || token.Start == 0 || UserTyped.Contains(word))
            {
                continue;
            }

            // "u.name" typed before the FROM that declares u: nothing can know
            // u's columns yet, so left to right it is only owed once FROM is in.
            var aliasQualified = t >= 2 && tokens[t - 1].Kind == SqlTokenKind.Dot
                && UserTyped.Contains(sql[tokens[t - 2].Start..tokens[t - 2].End]);

            foreach (var typed in new[] { 0, 1 })
            {
                var caret = token.Start + typed;
                var leftToRight = sql[..caret];
                if (!aliasQualified || leftToRight.Contains("FROM", StringComparison.Ordinal))
                {
                    Check(leftToRight, caret, "typing");
                }

                // Going back to fill in one word of an otherwise finished statement.
                Check(sql[..caret] + sql[token.End..], caret, "filling in");
            }

            void Check(string text, int caret, string how)
            {
                var ranked = CompletionRanker.Rank(
                    provider.GetCompletionData(text, caret), text[token.Start..caret], d => d.Text, d => d.Priority, _ => int.MaxValue);
                if (!ranked.Items.Any(i => string.Equals(i.Text, word, StringComparison.OrdinalIgnoreCase)
                    || i.Text.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase)))
                {
                    missing.Add($"{how}: {text.Insert(caret, "|")}  (expected {word})");
                }
            }
        }

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task Unquoted_names_fold_before_they_are_looked_up()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM PUBLIC.Users U WHERE u.|"));

        await Assert.That(columns).IsEquivalentTo(new[] { "id", "name" });
    }

    // --- F05 / T09: a shared column name is ambiguous ---

    [Test]
    public async Task A_column_two_sources_share_is_offered_qualified_per_source()
    {
        var items = At(Provider(), "SELECT * FROM public.users u\nJOIN public.orders o ON u.id = o.user_id\nWHERE |");
        var columns = ColumnInserts(items);

        await Assert.That(columns).Contains("u.id");
        await Assert.That(columns).Contains("o.id");
        await Assert.That(columns).DoesNotContain("id");
        await Assert.That(columns).Contains("name");
        await Assert.That(items.First(i => i.InsertText == "u.id").Label).IsEqualTo("u.id");
    }

    [Test]
    public async Task A_using_join_merges_the_shared_column()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM public.users u JOIN public.orders o USING (id) WHERE |"));

        await Assert.That(columns).Contains("id");
        await Assert.That(columns).DoesNotContain("u.id");
    }

    [Test]
    public async Task A_self_join_is_two_sources()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM public.users a JOIN public.users b ON a.id = b.id WHERE |"));

        await Assert.That(columns).IsEquivalentTo(new[] { "a.id", "a.name", "b.id", "b.name" });
    }

    [Test]
    public async Task An_unresolved_source_does_not_open_the_whole_catalog()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM nowhere n WHERE |"));

        await Assert.That(columns).IsEmpty();
    }

    // --- F06: a CTE's u.* is u's columns, not every source's ---

    [Test]
    public async Task A_cte_qualified_star_exposes_only_its_source()
    {
        var columns = ColumnInserts(At(Provider(),
            "WITH x AS (SELECT u.* FROM public.users u JOIN public.orders o ON true)\nSELECT x.| FROM x"));

        await Assert.That(columns).IsEquivalentTo(new[] { "id", "name" });
    }

    [Test]
    public async Task A_cte_shadows_a_same_named_table()
    {
        var columns = ColumnInserts(At(Provider(), "WITH users AS (SELECT 1 AS one) SELECT * FROM users u WHERE u.|"));

        await Assert.That(columns).IsEquivalentTo(new[] { "one" });
    }

    // --- F08: functions keep schema, quotes and kind ---

    [Test]
    public async Task A_function_off_the_search_path_inserts_qualified_and_quoted()
    {
        var items = At(Provider(), "SELECT My|");
        var myFunc = items.Single(i => i.Kind == SqlCompletionKind.Function && i.Text == "MyFunc");

        await Assert.That(myFunc.InsertText).IsEqualTo("custom.\"MyFunc\"()");
    }

    [Test]
    public async Task Same_named_functions_in_two_schemas_are_two_candidates()
    {
        var normalize = At(Provider(), "SELECT norm|").Where(i => i.Kind == SqlCompletionKind.Function && i.Text == "normalize").ToList();

        await Assert.That(normalize.Select(i => i.InsertText)).IsEquivalentTo(new[] { "normalize()", "custom.normalize()" });
    }

    [Test]
    public async Task Procedures_only_after_call_and_never_in_expressions()
    {
        var afterCall = At(Provider(), "CALL re|");
        await Assert.That(afterCall.Any(i => i.Text == "refresh_all")).IsTrue();
        await Assert.That(afterCall.Any(i => i.Kind == SqlCompletionKind.Column)).IsFalse();

        await Assert.That(At(Provider(), "SELECT re|").Any(i => i.Text == "refresh_all")).IsFalse();
    }

    [Test]
    public async Task A_schema_member_list_includes_its_functions()
    {
        var items = At(Provider(), "SELECT custom.|");

        await Assert.That(items.Any(i => i.Text == "MyFunc" && i.InsertText == "\"MyFunc\"()")).IsTrue();
    }

    // --- F09 / T21 / T34: JOIN suggestions ---

    [Test]
    public async Task Cross_join_does_not_boost_on_and_using()
    {
        var items = At(Provider(), "SELECT * FROM public.users u CROSS JOIN public.orders o |");

        await Assert.That(items.Any(i => i.Text == "ON" && i.Priority >= 200)).IsFalse();
    }

    [Test]
    public async Task An_excluded_schema_does_not_come_back_through_a_foreign_key()
    {
        var withAudit = At(Provider(), "SELECT * FROM public.orders o JOIN |");
        await Assert.That(withAudit.Any(i => i.Detail == "audit" && i.DescriptionText == "table · FK match")).IsTrue();

        var excluded = At(Provider(null, "audit"), "SELECT * FROM public.orders o JOIN |");
        await Assert.That(excluded.Any(i => i.Detail == "audit")).IsFalse();
    }

    [Test]
    public async Task The_join_condition_belongs_to_the_join_under_the_caret()
    {
        var items = At(Provider(), "SELECT * FROM public.orders o JOIN public.users u ON | JOIN audit.users a ON true");
        var condition = items.First(i => i.Kind == SqlCompletionKind.JoinCondition);

        // The joined table (u) first.
        await Assert.That(condition.InsertText).IsEqualTo("u.id = o.user_id");
    }

    // --- T10 / T11: literals and quoted identifiers ---

    [Test]
    [Arguments("SELECT E'can\\'t |'")]
    [Arguments("SELECT $tag1$hello |$tag1$")]
    public async Task Nothing_inside_literals(string marked)
    {
        await Assert.That(At(Provider(), marked)).IsEmpty();
    }

    [Test]
    public async Task Names_but_no_keywords_inside_an_open_quoted_identifier()
    {
        var items = At(Provider(), "SELECT * FROM public.users WHERE \"na|");

        await Assert.That(items.Any(i => i.Text == "name")).IsTrue();
        await Assert.That(items.Any(i => i.Kind == SqlCompletionKind.Keyword)).IsFalse();
    }

    // --- F07 / E: the provider's star expansion declines with a reason ---

    [Test]
    public async Task Star_expansion_declines_a_using_join_with_a_reason()
    {
        const string sql = "SELECT * FROM public.users u JOIN public.orders o USING (id)";
        var expansion = Provider().ExpandSelectStar(sql, sql.Length, out var refusal);

        await Assert.That(expansion).IsNull();
        await Assert.That(refusal).Contains("USING");
    }

    [Test]
    public async Task Star_expansion_resolves_through_the_search_path()
    {
        const string sql = "SELECT * FROM users";
        var expansion = Provider(["audit"]).ExpandSelectStar(sql, sql.Length, out _);

        await Assert.That(expansion?.Replacement).IsEqualTo("id, audit_only");
    }

    [Test]
    public async Task Keywords_are_there_before_any_catalog_arrives()
    {
        var provider = new SqlCompletionProvider(null);

        await Assert.That(At(provider, "sel|").Any(i => i.Text == "SELECT")).IsTrue();
        await Assert.That(At(provider, "SELECT coa|").Any(i => i.Text == "coalesce")).IsTrue();
    }

    // --- Second audit, package L: what is legal at the caret ---

    private static SqlCompletionProvider Stand()
    {
        var provider = new SqlCompletionProvider(null);
        provider.Load(PgNimbus.CompletionBench.AuditCatalog.Load(PgNimbus.CompletionBench.AuditCatalog.DefaultPath));
        return provider;
    }

    [Test]
    [Arguments("up|", "UPDATE")] // B04: not the unit_price column
    [Arguments("al|", "ALTER")]
    [Arguments("dr|", "DROP")]
    [Arguments("not|", "NOTIFY")]
    [Arguments("mer|", "MERGE")]
    [Arguments("SELECT 1;\nlis|", "LISTEN")]
    public async Task At_a_statement_start_only_commands_are_offered(string marked, string expected)
    {
        var items = At(Stand(), marked.Replace("\\n", "\n"));

        await Assert.That(items.All(i => i.Kind == SqlCompletionKind.Keyword)).IsTrue();
        await Assert.That(PreselectedItem(Stand(), marked.Replace("\\n", "\n")).Text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SELECT * FROM public.orders o WHERE o.id = 1 L|", "LIMIT")] // B02: not lag()
    [Arguments("SELECT * FROM public.orders o ORDER BY o.total_amount DESC L|", "LIMIT")]
    [Arguments("UPDATE public.customers SET is_active = false W|", "WHERE")] // not word_similarity()
    [Arguments("SELECT * FROM public.customers c WHERE c.email = 'x' a|", "AND")] // not avg()
    [Arguments("SELECT * FROM public.customers c JOIN public.orders o ON o.customer_id = c.id WH|", "WHERE")] // not WHEN
    [Arguments("SELECT * FROM public.customers c WHERE c.id = 1 o|", "OR")] // not ON
    [Arguments("SELECT * FROM public.customers c WHERE c.email IS |", "NULL")]
    [Arguments("SELECT * FROM public.customers c WHERE c.email IS NOT |", "NULL")]
    [Arguments("SELECT status FROM public.orders GROUP |", "BY")]
    [Arguments("SELECT * FROM public.orders ORDER BY id |", "DESC")]
    [Arguments("SELECT * FROM public.orders LIMIT 10 |", "OFFSET")]
    [Arguments("SELECT 1 UNION |", "SELECT")]
    [Arguments("INSERT |", "INTO")]
    [Arguments("DELETE |", "FROM")]
    [Arguments("SELECT * FROM public.orders WHERE status IN (SE|", "SELECT")]
    [Arguments("WITH r AS (SELECT 1) |", "SELECT")]
    [Arguments("SELECT * FROM public.customers WHERE a.deleted_at IS NU|", "NULL")] // not nullif()
    public async Task Only_what_can_follow_is_offered_and_the_likeliest_is_first(string marked, string expected)
    {
        await Assert.That(PreselectedItem(Stand(), marked).Text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SELECT * FROM public.customers c WHERE c.id = 1 |")]
    [Arguments("SELECT * FROM public.customers c WHERE c.email IS |")]
    [Arguments("SELECT * FROM public.orders ORDER BY id |")]
    public async Task After_a_finished_expression_no_column_or_function_is_offered(string marked)
    {
        await Assert.That(At(Stand(), marked).All(i => i.Kind == SqlCompletionKind.Keyword)).IsTrue();
    }

    [Test]
    [Arguments("SELECT * FROM or|")] // B02 prototype: OR must not crowd out orders
    [Arguments("SELECT * FROM public.orders WHERE or|")]
    [Arguments("SELECT now() - in|")] // interval, not IN
    [Arguments("INSERT IN|")]
    public async Task A_keyword_illegal_here_is_not_offered(string marked)
    {
        var keywords = At(Stand(), marked).Where(i => i.Kind == SqlCompletionKind.Keyword).Select(i => i.Text).ToList();

        await Assert.That(keywords).DoesNotContain("OR");
        await Assert.That(keywords).DoesNotContain("IN");
    }

    [Test]
    public async Task A_value_after_an_operator_doesnt_offer_not()
    {
        var keywords = At(Stand(), "SELECT * FROM public.orders WHERE order_date > no|").Where(i => i.Kind == SqlCompletionKind.Keyword).Select(i => i.Text).ToList();

        await Assert.That(keywords).DoesNotContain("NOT");
        await Assert.That(PreselectedItem(Stand(), "SELECT * FROM public.orders WHERE order_date > no|").Text).IsEqualTo("now");
    }

    [Test]
    public async Task Extension_machinery_and_partitions_are_not_candidates()
    {
        var provider = Stand();
        var functions = At(provider, "SELECT |").Where(i => i.Kind == SqlCompletionKind.Function).Select(i => i.Text).ToHashSet();

        // E03: I/O, operator implementations, index support — by catalog facts.
        foreach (var internalName in new[] { "ltree_in", "gtrgm_out", "hnsw_bit_support", "vector_lt", "word_similarity_commutator_op" })
        {
            await Assert.That(functions).DoesNotContain(internalName);
        }

        foreach (var callable in new[] { "similarity", "crypt", "l2_distance", "gen_random_uuid" })
        {
            await Assert.That(functions).Contains(callable);
        }

        // E04: one gen_random_uuid, not pg_catalog's and pgcrypto's.
        await Assert.That(At(provider, "SELECT gen_ran|").Count(i => i.Text == "gen_random_uuid")).IsEqualTo(1);

        // E05: iot.readings, not its eight partitions; they stay reachable, last, after "iot.".
        var tables = At(provider, "SELECT * FROM r|").Where(i => i.Kind == SqlCompletionKind.Table).Select(i => i.Text).ToList();
        await Assert.That(tables).Contains("readings");
        await Assert.That(tables.Any(t => t.StartsWith("readings_", StringComparison.Ordinal))).IsFalse();
        var iot = CompletionRanker.Rank(At(provider, "SELECT * FROM iot.|"), "", d => d.Text, d => d.Priority, _ => int.MaxValue).Items.Select(i => i.Text).ToList();
        await Assert.That(iot.IndexOf("readings")).IsLessThan(iot.IndexOf("readings_2026_01"));
    }

    [Test]
    public async Task No_bare_name_the_search_path_resolves_ever_preselects_another_schemas_relation()
    {
        var provider = Stand();
        var catalog = PgNimbus.CompletionBench.AuditCatalog.Load(PgNimbus.CompletionBench.AuditCatalog.DefaultPath);
        var failures = new List<string>();
        foreach (var table in catalog.Tables.Where(t => t.Schema == "public"))
        {
            foreach (var head in new[] { "SELECT * FROM ", "UPDATE ", "DELETE FROM ", "SELECT * FROM public.orders o JOIN " })
            {
                for (var typed = 1; typed <= table.Name.Length; typed++)
                {
                    var marked = head + table.Name[..typed] + "|";
                    var picked = PreselectedItem(provider, marked);
                    if (picked.Kind == SqlCompletionKind.Table && picked.Text == table.Name && picked.Detail != "public")
                    {
                        failures.Add($"{marked} → {picked.InsertText}");
                    }
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task In_an_on_condition_the_columns_a_foreign_key_ties_to_the_other_side_come_first()
    {
        // B07: issues has two FKs to users; either beats issues.id.
        var first = PreselectedItem(Stand(), "SELECT * FROM saas.issues i JOIN saas.users u ON u.id = i.|");
        await Assert.That(first.Text is "assignee_id" or "reporter_id").IsTrue();

        // The second join of users gets the FK the first one doesn't use yet.
        var second = PreselectedItem(Stand(),
            "SELECT * FROM saas.issues i JOIN saas.users a ON a.id = i.assignee_id JOIN saas.users r ON r.id = i.|");
        await Assert.That(second.Text).IsEqualTo("reporter_id");
    }

    // --- Second audit, package M: the keyword grammar (table C01, C02, C03) ---

    /// <summary>
    /// Table C01 of the audit, row by row: after each position the list is the
    /// keywords the "needed" column names — every one of them offered, nothing
    /// that isn't a keyword, and one of them preselected with nothing typed.
    /// </summary>
    [Test]
    [Arguments("SELECT * FROM public.customers c WHERE c.id = 1 |", "AND|OR|ORDER BY|GROUP BY|LIMIT|IS|IN|LIKE")]
    [Arguments("SELECT * FROM public.customers c WHERE c.email IS |", "NULL|NOT NULL|TRUE|FALSE|DISTINCT FROM")]
    [Arguments("SELECT * FROM public.customers c ORDER BY c.id |", "ASC|DESC|NULLS FIRST|NULLS LAST|LIMIT")]
    [Arguments("SELECT * FROM public.customers LIMIT 10 |", "OFFSET|FOR UPDATE")]
    [Arguments("SELECT id FROM public.customers UNION |", "SELECT|ALL|VALUES")]
    [Arguments("SELECT * FROM public.orders WHERE total_amount BETWEEN 1 |", "AND")]
    [Arguments("SELECT c.id, c.email |", "AS|FROM")]
    [Arguments("SELECT * FROM public.orders GROUP |", "BY")]
    [Arguments("SELECT * FROM public.orders ORDER |", "BY")]
    [Arguments("SELECT * FROM public.orders o WHERE EXISTS (|", "SELECT")]
    [Arguments("SELECT row_number() |", "OVER")]
    [Arguments("SELECT row_number() OVER (|", "PARTITION BY|ORDER BY")]
    [Arguments("SELECT count(*) FILTER (|", "WHERE")]
    [Arguments("SELECT CASE WHEN x THEN 1 |", "WHEN|ELSE|END")]
    [Arguments("INSERT INTO public.customers |", "VALUES|SELECT|DEFAULT VALUES")]
    [Arguments("INSERT INTO public.customers (first_name, email) |", "VALUES|SELECT")]
    [Arguments("INSERT INTO public.customers (id) VALUES (1) ON CONFLICT |", "DO NOTHING|DO UPDATE SET|ON CONSTRAINT")]
    [Arguments("INSERT INTO public.customers (id) VALUES (1) ON CONFLICT (id) DO |", "NOTHING|UPDATE SET")]
    public async Task C01_after_a_finished_expression_the_keywords_that_continue_it(string marked, string needed)
    {
        var items = At(Stand(), marked);
        var texts = items.Select(i => i.Text).ToList();
        var expected = needed.Split('|');

        foreach (var keyword in expected)
        {
            await Assert.That(texts).Contains(keyword);
        }

        // Keywords, and (package P) a whole construct where one fits: the
        // column list after INSERT INTO t.
        await Assert.That(items.All(i => i.Kind is SqlCompletionKind.Keyword or SqlCompletionKind.Snippet)).IsTrue();
        await Assert.That(expected.Contains(PreselectedItem(Stand(), marked).Text) || PreselectedItem(Stand(), marked).Kind == SqlCompletionKind.Snippet).IsTrue();
    }

    [Test]
    public async Task C01_merge_using_takes_a_relation()
    {
        var items = At(Stand(), "MERGE INTO public.customers c USING |");

        await Assert.That(items.Any(i => i.Kind == SqlCompletionKind.Table && i.Text == "orders")).IsTrue();
        await Assert.That(items.Any(i => i.Kind == SqlCompletionKind.Column)).IsFalse();
    }

    [Test]
    [Arguments("SELECT * FROM public.customers c WHERE c.id = 1 ob|", "ORDER BY")]
    [Arguments("SELECT status FROM public.orders WHERE total_amount > 1 gb|", "GROUP BY")]
    [Arguments("SELECT * FROM public.customers c lj|", "LEFT JOIN")]
    [Arguments("SELECT * FROM public.customers c WHERE c.email inn|", "IS NOT NULL")]
    [Arguments("SELECT * FROM public.customers c ORDER BY c.id nl|", "NULLS LAST")]
    [Arguments("SELECT row_number() OVER (pb|", "PARTITION BY")]
    [Arguments("ii|", "INSERT INTO")]
    [Arguments("df|", "DELETE FROM")]
    public async Task C02_a_multi_word_keyword_is_one_row_and_its_initials_find_it(string marked, string expected)
    {
        var item = PreselectedItem(Stand(), marked);

        await Assert.That(item.Text).IsEqualTo(expected);
        await Assert.That(item.InsertText).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SELECT |")]
    [Arguments("SELECT count(|")]
    public async Task C03_the_star_is_offered_where_it_can_go(string marked)
    {
        await Assert.That(At(Stand(), marked).Any(i => i.Text == "*")).IsTrue();
    }

    [Test]
    [Arguments("SELECT * FROM public.orders WHERE |")]
    [Arguments("SELECT sum(|")]
    [Arguments("SELECT id, |")]
    public async Task C03_the_star_is_not_offered_where_it_cannot(string marked)
    {
        await Assert.That(At(Stand(), marked).Any(i => i.Text == "*")).IsFalse();
    }

    [Test]
    public async Task C03_with_sources_the_star_comes_after_their_columns()
    {
        var ranked = CompletionRanker.Rank(At(Stand(), "SELECT | FROM public.customers c"), "", d => d.Text, d => d.Priority, _ => int.MaxValue)
            .Items.Select(i => i.Text).ToList();

        await Assert.That(ranked.IndexOf("*")).IsGreaterThan(ranked.IndexOf("email"));
        await Assert.That(ranked.IndexOf("*")).IsLessThan(ranked.IndexOf("count"));
    }

    [Test]
    public async Task C01_an_aggregate_call_can_be_followed_by_filter_or_over_a_plain_function_cannot()
    {
        var afterCount = At(Stand(), "SELECT count(*) |").Select(i => i.Text).ToList();
        var afterLower = At(Stand(), "SELECT lower(email) |").Select(i => i.Text).ToList();

        await Assert.That(afterCount).Contains("OVER");
        await Assert.That(afterCount).Contains("FILTER");
        await Assert.That(afterCount).Contains("FROM");
        await Assert.That(afterLower).DoesNotContain("OVER");
        await Assert.That(afterLower).Contains("FROM");
    }

    // --- Second audit, package O: a wider catalog (E01–E07) ---

    [Test]
    [Arguments("SELECT * FROM pg_stat_act|", "pg_stat_activity", "pg_stat_activity")] // E01
    [Arguments("SELECT * FROM pg|", "pg_stat_activity", "pg_stat_activity")] // the everyday one first
    [Arguments("SELECT * FROM pg_cla|", "pg_class", "pg_class")]
    [Arguments("SELECT * FROM information_schema.col|", "columns", "columns")]
    [Arguments("SELECT * FROM inf|", "information_schema", "information_schema")]
    [Arguments("SELECT * FROM cu|", "customers", "customers")] // the user's own still first
    [Arguments("SELECT pg_size_pr|", "pg_size_pretty", "pg_size_pretty()")] // E02
    [Arguments("SELECT to_timest|", "to_timestamp", "to_timestamp()")]
    [Arguments("SELECT pg_terminate_b|", "pg_terminate_backend", "pg_terminate_backend()")]
    [Arguments("SELECT pid FROM pg_stat_activity WHERE st|", "state", "state")]
    [Arguments("SELECT relname FROM pg_class WHERE relk|", "relkind", "relkind")]
    public async Task System_relations_and_builtin_functions_are_candidates(string marked, string expected, string insert)
    {
        var item = PreselectedItem(Stand(), marked);

        await Assert.That(item.Text).IsEqualTo(expected);
        await Assert.That(item.InsertText).IsEqualTo(insert);
    }

    [Test]
    public async Task Machinery_stays_out_of_the_builtins_and_information_schemas_plumbing_too()
    {
        var functions = At(Stand(), "SELECT |").Where(i => i.Kind == SqlCompletionKind.Function).Select(i => i.Text).ToHashSet();
        foreach (var internalName in new[] { "int4in", "texteq", "int4_sum", "eqsel", "bthandler", "textcat" })
        {
            await Assert.That(functions).DoesNotContain(internalName);
        }

        await Assert.That(functions.Count(f => f == "now")).IsEqualTo(1);
        var infoSchema = At(Stand(), "SELECT * FROM information_schema.|").Select(i => i.Text).ToList();
        await Assert.That(infoSchema).Contains("columns");
        await Assert.That(infoSchema.Any(t => t.StartsWith("_pg_", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("SELECT * FROM saas.issues i WHERE i.status = |", "open", "'open'")] // E07
    [Arguments("SELECT * FROM saas.issues i WHERE i.status = in|", "in_progress", "'in_progress'")]
    [Arguments("SELECT * FROM saas.issues i WHERE i.status IN ('open', |", "open", "'open'")]
    [Arguments("SELECT * FROM saas.issues i WHERE status <> |", "open", "'open'")]
    [Arguments("UPDATE saas.issues SET status = |", "open", "'open'")]
    [Arguments("SELECT * FROM saas.issues i WHERE i.status = 'bl|'", "blocked", "blocked")] // inside the quotes
    [Arguments("SELECT * FROM public.customers c WHERE c.is_active = t|", "TRUE", "TRUE")]
    [Arguments("SELECT * FROM public.customers c WHERE c.is_active = |", "TRUE", "TRUE")]
    public async Task A_comparison_with_an_enum_or_boolean_column_offers_its_values(string marked, string expected, string insert)
    {
        var item = PreselectedItem(Stand(), marked);

        await Assert.That(item.Text).IsEqualTo(expected);
        await Assert.That(item.InsertText).IsEqualTo(insert);
    }

    [Test]
    [Arguments("SELECT * FROM public.customers c WHERE c.email = 'x|'")]
    [Arguments("SELECT 'plain text |'")]
    [Arguments("SELECT * FROM public.customers -- c.is_active = |")]
    public async Task An_ordinary_string_or_comment_still_gets_nothing(string marked)
    {
        await Assert.That(At(Stand(), marked)).IsEmpty();
    }

    [Test]
    public async Task Nextval_date_trunc_and_extract_take_their_own_values()
    {
        var sequences = At(Stand(), "SELECT nextval('|')");
        await Assert.That(sequences.All(i => i.Kind == SqlCompletionKind.Sequence)).IsTrue();
        await Assert.That(sequences.Any(i => i.InsertText == "customers_id_seq")).IsTrue(); // on the path: bare
        await Assert.That(sequences.Any(i => i.InsertText == "commerce.products_id_seq")).IsTrue();

        await Assert.That(PreselectedItem(Stand(), "SELECT date_trunc('mo|', now())").InsertText).IsEqualTo("month");
        var fields = At(Stand(), "SELECT extract(|");
        await Assert.That(fields.Select(i => i.Text)).Contains("EPOCH");
        await Assert.That(fields.Any(i => i.Kind == SqlCompletionKind.Column)).IsFalse();
    }

    [Test]
    public async Task Rows_describe_what_they_name()
    {
        // E06: a column's key, reference and nullability; a relation's kind; a function's defaults.
        var customerId = At(Stand(), "SELECT * FROM public.orders o WHERE o.|").First(i => i.Text == "customer_id");
        await Assert.That(customerId.DescriptionText).Contains("→ customers.id");
        var id = At(Stand(), "SELECT * FROM public.orders o WHERE o.|").First(i => i.Text == "id");
        await Assert.That(id.DescriptionText).Contains("PK");

        var view = At(Stand(), "SELECT * FROM saas.|").First(i => i.Text == "account_seats");
        await Assert.That(view.DescriptionText).StartsWith("view");

        var mrr = At(Stand(), "SELECT saas.|").First(i => i.Text == "account_mrr");
        await Assert.That(mrr.DescriptionText).Contains("DEFAULT");
    }

    [Test]
    [Arguments("SELECT pid, state, query|", "query")] // not query_string, not querytree()
    [Arguments("SELECT query|", "query")]
    public async Task A_word_typed_in_full_keeps_its_own_row_first_before_any_from(string marked, string expected)
    {
        // What stops Enter from swapping a finished word for a longer catalog
        // name: the name equal to what was typed ranks first (package K).
        await Assert.That(PreselectedItem(Stand(), marked).Text).IsEqualTo(expected);
    }

    [Test]
    public async Task An_argument_hint_shows_which_arguments_have_defaults()
    {
        var hint = HintsAt(Stand(), "SELECT saas.account_mrr(1, |")!.Value.Hints.Single();

        await Assert.That(hint.Parameters[1].HasDefault).IsTrue();
        await Assert.That(hint.Parameters[1].Text).Contains("DEFAULT");
        await Assert.That(hint.Parameters[0].HasDefault).IsFalse();
    }

    // --- Second audit, package P: whole constructs (§6.4) ---

    [Test]
    [Arguments("SELECT * FROM public.orders o JOIN |", "customers c ON c.id = o.customer_id")]
    [Arguments("SELECT * FROM public.orders o JOIN cu|", "customers c ON c.id = o.customer_id")]
    [Arguments("SELECT * FROM saas.issues i JOIN saas.|", "users u ON u.id = i.assignee_id")]
    [Arguments("SELECT * FROM saas.issues i JOIN |", "saas.users u ON u.id = i.reporter_id")]
    public async Task After_join_a_table_comes_with_its_alias_and_foreign_key_condition(string marked, string insert)
    {
        var items = At(Stand(), marked);

        await Assert.That(items.Any(i => i.Kind == SqlCompletionKind.Snippet && i.InsertText == insert)).IsTrue();
    }

    [Test]
    public async Task A_join_row_sits_right_under_its_table_and_the_plain_table_stays_first()
    {
        var ranked = CompletionRanker.Rank(At(Stand(), "SELECT * FROM public.orders o JOIN |"), "", d => d.Text, d => d.Priority, _ => int.MaxValue).Items;
        var customers = ranked.ToList().FindIndex(i => i.Kind == SqlCompletionKind.Table && i.Text == "customers");

        await Assert.That(ranked[customers + 1].InsertText).IsEqualTo("customers c ON c.id = o.customer_id");
        await Assert.That(PreselectedItem(Stand(), "SELECT * FROM public.orders o JOIN cust|").Kind).IsEqualTo(SqlCompletionKind.Table);
    }

    [Test]
    public async Task After_insert_into_a_table_its_column_list_comes_whole()
    {
        var items = At(Stand(), "INSERT INTO public.customers |");
        var list = items.First(i => i.Kind == SqlCompletionKind.Snippet);

        await Assert.That(list.InsertText).StartsWith("(first_name, last_name, email");
        await Assert.That(list.InsertText).EndsWith(") VALUES ()");
        await Assert.That(list.CaretIndex).IsEqualTo(list.InsertText.Length - 1);
        await Assert.That(list.InsertText).DoesNotContain("(id,"); // an identity/serial key isn't the row's to give
    }

    [Test]
    [Arguments("SELECT c.email, count(*) FROM public.customers c GROUP BY |", "c.email")]
    [Arguments("SELECT status, c.email, sum(total_amount) AS s FROM public.orders o JOIN public.customers c ON c.id = o.customer_id GROUP BY |", "status, c.email")]
    [Arguments("SELECT i.status, count(*) FILTER (WHERE i.priority <= 2) AS urgent FROM saas.issues i GROUP BY |", "i.status")]
    public async Task After_group_by_the_select_lists_non_aggregates_come_whole(string marked, string expected)
    {
        var item = PreselectedItem(Stand(), marked);

        await Assert.That(item.Kind).IsEqualTo(SqlCompletionKind.Snippet);
        await Assert.That(item.InsertText).IsEqualTo(expected);
    }

    [Test]
    public async Task Group_by_offers_no_list_when_nothing_is_aggregated()
    {
        await Assert.That(At(Stand(), "SELECT status FROM public.orders GROUP BY |").Any(i => i.Kind == SqlCompletionKind.Snippet)).IsFalse();
    }

    [Test]
    public async Task After_select_every_column_of_the_sources_comes_as_one_row()
    {
        var items = At(Stand(), "SELECT | FROM public.orders o");

        await Assert.That(items.Any(i => i.Kind == SqlCompletionKind.Snippet
            && i.InsertText == "o.id, o.customer_id, o.order_date, o.status, o.total_amount")).IsTrue();
    }

    [Test]
    public async Task Right_after_a_star_the_star_spelled_out_replaces_it()
    {
        var sql = "SELECT * FROM public.orders";
        var items = Stand().GetCompletionData(sql, "SELECT *".Length);
        var expansion = items.First(i => i.Kind == SqlCompletionKind.Snippet);

        await Assert.That(expansion.InsertText).IsEqualTo("id, customer_id, order_date, status, total_amount");
        await Assert.That(expansion.ReplaceFrom).IsEqualTo("SELECT ".Length);
    }

    [Test]
    [Arguments("SELECT row_num|", "row_number() OVER ()", 19)]
    [Arguments("SELECT la|", "lag() OVER ()", 4)]
    public async Task A_window_function_comes_with_its_window(string marked, string insert, int caret)
    {
        var item = PreselectedItem(Stand(), marked);

        await Assert.That(item.InsertText).IsEqualTo(insert);
        await Assert.That(item.CaretIndex).IsEqualTo(caret);
    }

    [Test]
    public async Task After_an_aggregate_over_and_filter_come_with_their_parentheses()
    {
        var items = At(Stand(), "SELECT count(*) |");

        await Assert.That(items.Single(i => i.Text == "FILTER").InsertText).IsEqualTo("FILTER (WHERE )");
        await Assert.That(items.Single(i => i.Text == "OVER").InsertText).IsEqualTo("OVER ()");
    }

    [Test]
    public async Task Do_update_set_offers_the_excluded_assignment()
    {
        var items = At(Stand(), "INSERT INTO public.customers (email, first_name) VALUES ('a', 'b') ON CONFLICT (email) DO UPDATE SET |");

        await Assert.That(items.Any(i => i.InsertText == "first_name = excluded.first_name")).IsTrue();
    }

    [Test]
    [Arguments("INSERT INTO public.customers (first_name, email) VALUES ('Ada', |", 1, "email")]
    [Arguments("INSERT INTO public.customers (first_name, email) VALUES ('Ada', 'x'), (|", 0, "first_name")]
    [Arguments("INSERT INTO public.customers VALUES (1, |", 1, "first_name")]
    public async Task A_values_row_hints_the_column_its_value_goes_into(string marked, int active, string column)
    {
        var hint = HintsAt(Stand(), marked)!.Value.Hints.Single();

        await Assert.That(hint.Name).IsEqualTo("VALUES");
        await Assert.That(hint.ActiveParameter).IsEqualTo(active);
        await Assert.That(hint.Parameters[active].Name).IsEqualTo(column);
    }

    // --- Second audit, package N: DDL and utility statements (D01, D02, appendix B 20–25) ---

    [Test]
    [Arguments("CREATE |", "TABLE", SqlCompletionKind.Keyword)] // 20
    [Arguments("ALTER TABLE public.customers DROP COLUMN |", "email", SqlCompletionKind.Column)] // 21
    [Arguments("ALTER TABLE public.customers ADD COLUMN x |", "text", SqlCompletionKind.Type)] // 22
    [Arguments("CREATE INDEX idx ON public.orders (|)", "customer_id", SqlCompletionKind.Column)] // 23
    [Arguments("SET |", "work_mem", SqlCompletionKind.Setting)] // 24
    [Arguments("EXPLAIN (|", "BUFFERS", SqlCompletionKind.Keyword)] // 25
    [Arguments("CREATE TABLE t (id |", "bigint", SqlCompletionKind.Type)]
    [Arguments("CREATE TABLE t (id bigint REFERENCES |", "customers", SqlCompletionKind.Table)]
    [Arguments("CREATE TABLE t (id bigint REFERENCES saas.users (|", "id", SqlCompletionKind.Column)]
    [Arguments("CREATE INDEX ON |", "orders", SqlCompletionKind.Table)]
    [Arguments("CREATE INDEX i ON orders USING |", "gin", SqlCompletionKind.Value)]
    [Arguments("CREATE INDEX i ON orders USING |", "hnsw", SqlCompletionKind.Value)] // pgvector is installed on the stand
    [Arguments("ALTER TABLE customers |", "ADD COLUMN", SqlCompletionKind.Keyword)]
    [Arguments("ALTER TABLE customers ALTER COLUMN |", "is_active", SqlCompletionKind.Column)]
    [Arguments("ALTER TABLE customers RENAME COLUMN |", "last_name", SqlCompletionKind.Column)]
    [Arguments("DROP VIEW |", "account_seats", SqlCompletionKind.Table)]
    [Arguments("DROP FUNCTION |", "account_mrr", SqlCompletionKind.Function)]
    [Arguments("DROP SCHEMA |", "saas", SqlCompletionKind.Schema)]
    [Arguments("DROP INDEX |", "customers_email_key", SqlCompletionKind.Index)]
    [Arguments("COMMENT ON TABLE |", "customers", SqlCompletionKind.Table)]
    [Arguments("GRANT SELECT ON saas.issues TO |", "postgres", SqlCompletionKind.Role)]
    [Arguments("TRUNCATE |", "orders", SqlCompletionKind.Table)]
    [Arguments("VACUUM ANALYZE |", "orders", SqlCompletionKind.Table)]
    [Arguments("REFRESH MATERIALIZED VIEW |", "mv_daily_sales", SqlCompletionKind.Table)]
    [Arguments("CREATE EXTENSION |", "pg_stat_statements", SqlCompletionKind.Extension)]
    [Arguments("DROP EXTENSION |", "vector", SqlCompletionKind.Extension)]
    [Arguments("SET search_path TO |", "saas", SqlCompletionKind.Schema)]
    [Arguments("SET client_min_messages TO |", "notice", SqlCompletionKind.Value)]
    [Arguments("SHOW |", "statement_timeout", SqlCompletionKind.Setting)]
    [Arguments("COPY |", "orders", SqlCompletionKind.Table)]
    public async Task A_ddl_or_utility_slot_offers_what_goes_there(string marked, string expected, SqlCompletionKind kind)
    {
        var items = At(Stand(), marked);

        await Assert.That(items.Any(i => i.Text == expected && i.Kind == kind)).IsTrue();
    }

    [Test]
    [Arguments("ALTER TABLE public.customers DROP COLUMN |")]
    [Arguments("CREATE INDEX idx ON public.orders (|)")]
    public async Task A_column_slot_holds_that_relations_columns_only(string marked)
    {
        var items = At(Stand(), marked);
        var owner = marked.Contains("customers", StringComparison.Ordinal) ? "customers" : "orders";

        await Assert.That(items.Where(i => i.Kind == SqlCompletionKind.Column).All(i => i.DescriptionText!.Contains($"column · {owner}", StringComparison.Ordinal))).IsTrue();
        await Assert.That(items.Any(i => i.Kind is SqlCompletionKind.Table or SqlCompletionKind.Function)).IsFalse();
    }

    [Test]
    public async Task Drop_function_writes_the_overloads_argument_types()
    {
        var mrr = At(Stand(), "DROP FUNCTION |").First(i => i.Text == "account_mrr");

        await Assert.That(mrr.InsertText).IsEqualTo("saas.account_mrr(p_account_id bigint, p_at date)");
    }

    [Test]
    public async Task Drop_view_offers_views_not_tables()
    {
        var names = At(Stand(), "DROP VIEW |").Where(i => i.Kind == SqlCompletionKind.Table).Select(i => i.Text).ToList();

        await Assert.That(names).Contains("account_seats");
        await Assert.That(names).DoesNotContain("customers");
    }

    [Test]
    public async Task A_created_objects_name_takes_an_existing_schema_and_the_schemas_objects_follow_the_slots_kind()
    {
        await Assert.That(At(Stand(), "CREATE TABLE |").Any(i => i.Kind == SqlCompletionKind.Schema && i.Text == "saas")).IsTrue();

        var views = At(Stand(), "DROP VIEW IF EXISTS saas.|");
        await Assert.That(views.Select(i => i.Text)).Contains("account_seats");
        await Assert.That(views.Select(i => i.Text)).DoesNotContain("issues");
        await Assert.That(views.First(i => i.Text == "account_seats").InsertText).IsEqualTo("account_seats");

        await Assert.That(At(Stand(), "DROP FUNCTION saas.|").First(i => i.Text == "account_mrr").InsertText)
            .IsEqualTo("account_mrr(p_account_id bigint, p_at date)");
    }

    [Test]
    [Arguments("CREATE TABLE t (id bi|", "bigint")]
    [Arguments("CREATE TABLE t (id te|", "text")]
    [Arguments("SELECT x::ti|", "timestamptz")]
    public async Task The_types_declared_most_come_first(string marked, string expected)
    {
        await Assert.That(PreselectedItem(Stand(), marked).Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Listen_offers_the_monitors_channels()
    {
        var provider = Stand();
        provider.NotifyChannels = ["orders_changed"];

        await Assert.That(At(provider, "LISTEN |").Any(i => i.Text == "orders_changed")).IsTrue();
    }

    [Test]
    public async Task After_a_schema_in_a_join_the_tables_a_foreign_key_connects_come_first()
    {
        await Assert.That(PreselectedItem(Stand(), "SELECT * FROM saas.teams t JOIN saas.te|").Text).IsEqualTo("team_members");
    }

    // --- Package F: scopes (T13–T19) ---

    // Columns the caret's own block contributes (the top band), as inserted.
    private static string[] ScopeColumns(IEnumerable<SqlCompletionData> items) =>
        [.. items.Where(i => i.Kind == SqlCompletionKind.Column && i.Priority >= 95).Select(i => i.InsertText)];

    [Test]
    public async Task T13_An_exists_subquery_leaks_nothing_into_the_outer_where()
    {
        var columns = ColumnInserts(At(Provider(),
            "SELECT * FROM public.users u WHERE EXISTS (SELECT 1 FROM public.orders o WHERE o.user_id = u.id) AND |"));

        await Assert.That(columns).IsEquivalentTo(new[] { "id", "name" });
    }

    [Test]
    public async Task T13_Inside_exists_the_outer_level_is_offered_qualified()
    {
        var columns = ColumnInserts(At(Provider(),
            "SELECT * FROM public.users u WHERE EXISTS (SELECT 1 FROM public.orders o WHERE |)"));

        await Assert.That(columns).Contains("user_id");
        await Assert.That(columns).Contains("total");
        await Assert.That(columns).Contains("u.name");
        await Assert.That(columns).DoesNotContain("name");
        await Assert.That(columns).DoesNotContain("audit_only");
    }

    [Test]
    public async Task T14_An_inner_alias_hides_the_outer_one_only_inside()
    {
        var inner = ColumnInserts(At(Provider(), "SELECT * FROM public.users x WHERE EXISTS (SELECT 1 FROM public.orders x WHERE x.|)"));
        await Assert.That(inner).IsEquivalentTo(new[] { "id", "user_id", "total" });

        var outer = ColumnInserts(At(Provider(), "SELECT * FROM public.users x WHERE EXISTS (SELECT 1 FROM public.orders x) AND x.|"));
        await Assert.That(outer).IsEquivalentTo(new[] { "id", "name" });
    }

    [Test]
    public async Task T15_A_derived_table_exposes_its_output_names()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT q.| FROM (SELECT id AS customer_id FROM public.users) q"));

        await Assert.That(columns).IsEquivalentTo(new[] { "customer_id" });
    }

    [Test]
    public async Task T15_A_derived_star_and_a_column_alias_list_resolve()
    {
        var star = At(Provider(), "SELECT q.| FROM (SELECT * FROM public.users) q");
        await Assert.That(ColumnInserts(star)).IsEquivalentTo(new[] { "id", "name" });
        await Assert.That(star.First(i => i.Text == "name").Detail).IsEqualTo("text");

        var renamed = ColumnInserts(At(Provider(), "SELECT q.| FROM (SELECT id, name FROM public.users) q(uid)"));
        await Assert.That(renamed).IsEquivalentTo(new[] { "uid", "name" });
    }

    [Test]
    public async Task T16_A_cte_chain_resolves_through_each_link()
    {
        var columns = ColumnInserts(At(Provider(),
            "WITH a AS (SELECT id AS aid FROM public.users), b AS (SELECT * FROM a) SELECT b.| FROM b"));

        await Assert.That(columns).IsEquivalentTo(new[] { "aid" });
    }

    [Test]
    public async Task T16_A_recursive_cte_names_its_columns_from_the_first_branch()
    {
        var columns = ColumnInserts(At(Provider(),
            "WITH RECURSIVE r AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM r WHERE n < 5) SELECT r.| FROM r"));
        await Assert.That(columns).IsEquivalentTo(new[] { "n" });

        // A star that reaches the CTE itself terminates rather than recursing forever.
        var self = ColumnInserts(At(Provider(), "WITH RECURSIVE r AS (SELECT * FROM r) SELECT r.| FROM r"));
        await Assert.That(self).IsEmpty();
    }

    [Test]
    public async Task T16_A_cte_declared_list_wins_over_its_body()
    {
        var columns = ColumnInserts(At(Provider(), "WITH x (a, b) AS (SELECT id, name FROM public.users) SELECT x.| FROM x"));

        await Assert.That(columns).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task T16_A_cte_inside_a_subquery_is_not_offered_outside_it()
    {
        var items = At(Provider(), "SELECT * FROM (WITH inner_c AS (SELECT 1 AS v) SELECT * FROM inner_c) d JOIN |");

        await Assert.That(items.Any(i => i.Kind == SqlCompletionKind.Cte)).IsFalse();
    }

    [Test]
    public async Task T17_Dml_returning_and_values_ctes_expose_their_names()
    {
        var returning = ColumnInserts(At(Provider(), "WITH x AS (DELETE FROM public.orders RETURNING id) SELECT x.| FROM x"));
        await Assert.That(returning).IsEquivalentTo(new[] { "id" });

        var values = ColumnInserts(At(Provider(), "WITH v AS (VALUES (1, 'a')) SELECT v.| FROM v"));
        await Assert.That(values).IsEquivalentTo(new[] { "column1", "column2" });
    }

    [Test]
    public async Task T18_A_from_subquery_cannot_name_its_siblings()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM public.users u, (SELECT * FROM public.orders o WHERE |) d"));

        await Assert.That(columns).IsEquivalentTo(new[] { "id", "user_id", "total" });
    }

    [Test]
    public async Task T18_A_lateral_subquery_can()
    {
        var columns = ColumnInserts(At(Provider(), "SELECT * FROM public.users u, LATERAL (SELECT * FROM public.orders o WHERE o.user_id = |) d"));

        await Assert.That(columns).Contains("u.id");
        await Assert.That(columns).Contains("u.name");
        await Assert.That(columns).Contains("total");
    }

    [Test]
    public async Task T19_Union_branches_keep_their_own_sources()
    {
        var first = ScopeColumns(At(Provider(), "SELECT | FROM public.users u UNION SELECT total FROM public.orders o"));
        await Assert.That(first).IsEquivalentTo(new[] { "id", "name" });

        var second = At(Provider(), "SELECT name FROM public.users u UNION SELECT | FROM public.orders o");
        await Assert.That(ColumnInserts(second)).IsEquivalentTo(new[] { "id", "user_id", "total" });
    }

    [Test]
    public async Task Nesting_past_the_limit_offers_no_columns_at_all()
    {
        var deep = "SELECT * FROM public.users u WHERE " + string.Concat(Enumerable.Repeat("EXISTS (SELECT 1 FROM public.orders WHERE ", 60)) + "|";

        await Assert.That(ColumnInserts(At(Provider(), deep))).IsEmpty();
    }

    // --- Package G: PostgreSQL statement contexts and JOIN (T20–T25) ---

    [Test]
    public async Task T24_Insert_column_list_offers_the_targets_columns_not_yet_listed()
    {
        var items = At(Provider(), "INSERT INTO public.orders (id, |) VALUES (1, 2, 3)");

        await Assert.That(ColumnInserts(items)).IsEquivalentTo(new[] { "user_id", "total" });
        await Assert.That(items.Any(i => i.Kind is SqlCompletionKind.Keyword or SqlCompletionKind.Function)).IsFalse();

        var open = ColumnInserts(At(Provider(), "INSERT INTO public.orders AS o (|"));
        await Assert.That(open).IsEquivalentTo(new[] { "id", "user_id", "total" });
    }

    [Test]
    public async Task T24_Set_offers_only_target_columns_left_of_the_equals_sign()
    {
        var left = At(Provider(), "UPDATE public.orders o SET total = 0, | FROM public.users u WHERE o.user_id = u.id");
        await Assert.That(ColumnInserts(left)).IsEquivalentTo(new[] { "id", "user_id" });
        await Assert.That(left.Any(i => i.Kind != SqlCompletionKind.Column)).IsFalse();

        var right = ColumnInserts(At(Provider(), "UPDATE public.orders o SET total = | FROM public.users u"));
        await Assert.That(right).Contains("name");
        await Assert.That(right).Contains("user_id");
        await Assert.That(right).DoesNotContain("audit_only");
    }

    [Test]
    public async Task T24_On_conflict_target_and_do_update_set_use_the_target_and_excluded()
    {
        var target = ColumnInserts(At(Provider(), "INSERT INTO public.orders (id, total) VALUES (1, 2) ON CONFLICT (|"));
        await Assert.That(target).IsEquivalentTo(new[] { "id", "user_id", "total" });

        var setTarget = ColumnInserts(At(Provider(), "INSERT INTO public.orders (id, total) VALUES (1, 2) ON CONFLICT (id) DO UPDATE SET |"));
        await Assert.That(setTarget).IsEquivalentTo(new[] { "id", "user_id", "total" });

        var value = At(Provider(), "INSERT INTO public.orders (id, total) VALUES (1, 2) ON CONFLICT (id) DO UPDATE SET total = |");
        await Assert.That(ColumnInserts(value)).Contains("excluded.total");
        await Assert.That(ColumnInserts(value)).Contains("total");

        var member = ColumnInserts(At(Provider(), "INSERT INTO public.orders (id, total) VALUES (1, 2) ON CONFLICT (id) DO UPDATE SET total = excluded.|"));
        await Assert.That(member).IsEquivalentTo(new[] { "id", "user_id", "total" });
    }

    [Test]
    public async Task T24_Returning_names_the_statements_sources_and_never_excluded()
    {
        var delete = At(Provider(), "DELETE FROM public.orders o USING public.users u WHERE o.user_id = u.id RETURNING |");
        await Assert.That(ColumnInserts(delete)).Contains("total");
        await Assert.That(ColumnInserts(delete)).Contains("name");
        await Assert.That(ColumnInserts(delete)).DoesNotContain("audit_only");

        var insert = ColumnInserts(At(Provider(), "INSERT INTO public.orders (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET total = 0 RETURNING |"));
        await Assert.That(insert).Contains("total");
        await Assert.That(insert.Any(c => c.StartsWith("excluded.", StringComparison.Ordinal))).IsFalse();
        await Assert.That(insert).DoesNotContain("name");
    }

    [Test]
    public async Task T22_Join_using_offers_the_columns_both_sides_of_that_join_share()
    {
        var items = At(Provider(), "SELECT * FROM public.users u JOIN public.orders o USING (|)");
        await Assert.That(ColumnInserts(items)).IsEquivalentTo(new[] { "id" });
        await Assert.That(items.Any(i => i.Kind != SqlCompletionKind.Column)).IsFalse();

        var early = ColumnInserts(At(Provider(), "SELECT * FROM public.users u JOIN audit.users a USING (|) JOIN public.orders o ON true"));
        await Assert.That(early).IsEquivalentTo(new[] { "id" });

        var listed = ColumnInserts(At(Provider(), "SELECT * FROM public.users u JOIN audit.users a USING (id, |)"));
        await Assert.That(listed).IsEmpty();
    }

    [Test]
    public async Task T25_An_output_alias_is_offered_in_order_by_but_not_in_where()
    {
        var orderBy = ColumnInserts(At(Provider(), "SELECT total * 2 AS doubled FROM public.orders ORDER BY |"));
        await Assert.That(orderBy).Contains("doubled");
        await Assert.That(orderBy).Contains("total");

        var where = ColumnInserts(At(Provider(), "SELECT total * 2 AS doubled FROM public.orders WHERE |"));
        await Assert.That(where).DoesNotContain("doubled");
        await Assert.That(where).Contains("total");
    }

    private static SqlCompletionProvider TwoForeignKeysProvider()
    {
        var provider = new SqlCompletionProvider(null);
        provider.Load(new CompletionCatalog(
            ["public"],
            [
                new CompletionTable("public", "users", [Col("users", "id", "int4")]),
                new CompletionTable("public", "orders", [Col("orders", "id", "int4"), Col("orders", "buyer_id", "int4"), Col("orders", "seller_id", "int4")]),
            ],
            [],
            [
                new ForeignKeyInfo("public", "orders", ["buyer_id"], "public", "users", ["id"], "orders_buyer_fkey"),
                new ForeignKeyInfo("public", "orders", ["seller_id"], "public", "users", ["id"], "orders_seller_fkey"),
            ],
            ["public"]));
        return provider;
    }

    [Test]
    public async Task T20_Two_foreign_keys_between_a_pair_are_two_named_conditions()
    {
        var conditions = At(TwoForeignKeysProvider(), "SELECT * FROM public.users u JOIN public.orders o ON |")
            .Where(i => i.Kind == SqlCompletionKind.JoinCondition)
            .ToList();

        await Assert.That(conditions.Select(c => (c.InsertText, Detail: c.Detail ?? "<none>"))).IsEquivalentTo(new[]
        {
            ("o.buyer_id = u.id", "orders_buyer_fkey"),
            ("o.seller_id = u.id", "orders_seller_fkey"),
        });
    }

    [Test]
    public async Task T21_Natural_join_takes_no_condition()
    {
        var items = At(Provider(), "SELECT * FROM public.users u NATURAL JOIN public.orders o |");

        await Assert.That(items.Any(i => i.Text is "ON" or "USING" && i.Priority >= 200)).IsFalse();
    }

    // --- Package H: argument hints and cast types (T26, T27) ---

    private static SqlCompletionProvider TypedProvider()
    {
        var provider = new SqlCompletionProvider(null);
        provider.Load(Catalog(["public"]) with
        {
            BuiltinFunctions =
            [
                new CompletionFunction("pg_catalog", new FunctionInfo("round", "numeric", "numeric", 'f')),
                new CompletionFunction("pg_catalog", new FunctionInfo("round", "numeric, integer", "numeric", 'f')),
            ],
            Types =
            [
                new DataTypeInfo("pg_catalog", "int4", "integer", 'b'),
                new DataTypeInfo("pg_catalog", "timestamptz", "timestamp with time zone", 'b'),
                new DataTypeInfo("public", "email", "email", 'd'),
                new DataTypeInfo("custom", "Mood", "custom.\"Mood\"", 'e'),
            ],
        });
        return provider;
    }

    private static (SqlCallSite Site, IReadOnlyList<SignatureHint> Hints)? HintsAt(SqlCompletionProvider provider, string marked)
    {
        var caret = marked.IndexOf('|');
        return provider.GetSignatureHints(marked.Remove(caret, 1), caret);
    }

    [Test]
    public async Task T27_Builtin_overloads_hint_the_argument_being_typed()
    {
        var hints = HintsAt(TypedProvider(), "SELECT round(total, |) FROM public.orders")!.Value.Hints;

        await Assert.That(hints.Single().Parameters.Select(p => p.Text)).IsEquivalentTo(new[] { "numeric", "integer" }, CollectionOrdering.Matching);
        await Assert.That(hints.Single().ActiveParameter).IsEqualTo(1);
    }

    [Test]
    public async Task T26_A_qualified_call_hints_that_schemas_overloads_only()
    {
        var qualified = HintsAt(TypedProvider(), "SELECT custom.normalize(|")!.Value.Hints;
        await Assert.That(qualified.Select(h => h.Schema)).IsEquivalentTo(new[] { "custom" });

        // Unqualified, only what the search_path reaches.
        var bare = HintsAt(TypedProvider(), "SELECT normalize(|")!.Value.Hints;
        await Assert.That(bare.Select(h => h.Schema)).IsEquivalentTo(new[] { "public" });

        await Assert.That(HintsAt(TypedProvider(), "SELECT refresh_all(|")).IsNull(); // a procedure is not a call in an expression
        await Assert.That(HintsAt(TypedProvider(), "SELECT nosuch(|")).IsNull();
    }

    [Test]
    public async Task A_cast_offers_types_and_nothing_else()
    {
        var items = At(TypedProvider(), "SELECT total::| FROM public.orders");

        await Assert.That(items.All(i => i.Kind == SqlCompletionKind.Type)).IsTrue();
        await Assert.That(items.Select(i => i.InsertText)).IsEquivalentTo(new[] { "integer", "timestamptz", "email", "custom.\"Mood\"" });
    }

    [Test]
    public async Task Cast_as_offers_types_too()
    {
        var items = At(TypedProvider(), "SELECT CAST(total AS tim|) FROM public.orders");

        await Assert.That(items.Any(i => i.InsertText == "timestamptz")).IsTrue();
        await Assert.That(items.Any(i => i.Kind != SqlCompletionKind.Type)).IsFalse();

        // An alias after AS is not a type position.
        await Assert.That(At(TypedProvider(), "SELECT total AS t| FROM public.orders").Any(i => i.Kind == SqlCompletionKind.Type)).IsFalse();
    }

    [Test]
    public async Task A_cast_before_the_catalog_arrives_still_offers_the_everyday_types()
    {
        var items = At(new SqlCompletionProvider(null), "SELECT '1'::|");

        await Assert.That(items.Select(i => i.InsertText)).Contains("integer");
        await Assert.That(items.Select(i => i.InsertText)).Contains("jsonb");
    }

    // --- Second audit, package R (E08): columns typed before their FROM ---

    [Test]
    [Arguments("SELECT c.fi|", "first_name", "customers")]
    [Arguments("SELECT oi.qu|", "quantity", "order_items")]
    [Arguments("SELECT inv.nu|", "number", "saas.invoices")]
    [Arguments("SELECT tm.ro|", "role", "saas.team_members")]
    [Arguments("SELECT p.ke|", "key", "saas.projects")]
    [Arguments("SELECT i.number, i.ti|", "title", "saas.issues")]
    [Arguments("SELECT count(u.id|", "id", "saas.users")]
    [Arguments("SELECT row_number() OVER (PARTITION BY o.cu|", "customer_id", "orders")]
    [Arguments("SELECT c.email, c.fi| FROM orders o", "first_name", "customers")] // editing: c still undeclared
    public async Task An_alias_declared_later_offers_the_columns_of_what_it_shortens(string marked, string column, string table)
    {
        var items = At(Stand(), marked);

        await Assert.That(items.Any(i => i.Text == column && i.Detail == table)).IsTrue();
        await Assert.That(PreselectedItem(Stand(), marked).Text).IsEqualTo(column);
    }

    [Test]
    public async Task The_table_the_path_finds_comes_before_one_it_does_not()
    {
        // "c" fits public.customers and saas.* tables starting with c; both have an email.
        var ranked = CompletionRanker.Rank(At(Stand(), "SELECT c.em|"), "em", d => d.Text, d => d.Priority, _ => int.MaxValue);

        await Assert.That(ranked.Items[ranked.SelectedIndex].Detail).IsEqualTo("customers");
    }

    [Test]
    public async Task A_cte_the_alias_shortens_is_read_too()
    {
        const string marked = "WITH recent AS (SELECT customer_id, max(order_date) AS last_order FROM orders GROUP BY customer_id)\nSELECT c.email, r.la|";

        await Assert.That(PreselectedItem(Stand(), marked) is { Text: "last_order", Detail: "recent" }).IsTrue();
    }

    [Test]
    [Arguments("SELECT * FROM customers c WHERE x.|")] // a predicate: the FROM is written, x is a typo
    [Arguments("SELECT c.| FROM customers c")] // declared: its own columns
    [Arguments("SELECT public.|")] // a schema
    public async Task Only_an_undeclared_name_in_a_select_list_is_guessed(string marked)
    {
        await Assert.That(At(Stand(), marked).Any(i => i.Kind == SqlCompletionKind.Column && i.DescriptionText?.Contains(" · if ") == true)).IsFalse();
    }

    [Test]
    public async Task A_bare_column_before_any_from_names_its_table_and_brings_the_from()
    {
        var items = At(Stand(), "SELECT first_n|");
        var row = items.First(i => i.Text == "first_name" && i.Detail == "customers");

        await Assert.That(row.AppendClause).IsEqualTo("FROM customers");
        // It stands in for the catalog-wide row of the same name.
        await Assert.That(items.Any(i => i.Text == "first_name" && i.AppendClause is null)).IsFalse();
        await Assert.That(items.Any(i => i.Text == "email" && i.Detail == "saas.users" && i.AppendClause == "FROM saas.users")).IsFalse();
        await Assert.That(At(Stand(), "SELECT em|").Any(i => i.Text == "email" && i.AppendClause == "FROM saas.users")).IsTrue();
        // The list opens by itself after "SELECT ", before a letter: the path's tables are in it already.
        await Assert.That(At(Stand(), "SELECT |").Any(i => i.Text == "first_name" && i.AppendClause == "FROM customers")).IsTrue();
    }

    [Test]
    [Arguments("SELECT first_n| FROM customers")]
    [Arguments("SELECT (SELECT first_n|)")]
    [Arguments("SELECT 1 UNION SELECT first_n|")]
    [Arguments("SELECT|")]
    [Arguments("SELECT count(*) AS ord|")] // a name being made up
    public async Task No_from_is_brought_where_one_is_written_or_would_not_fit(string marked)
    {
        await Assert.That(At(Stand(), marked).Any(i => i.AppendClause is not null)).IsFalse();
    }
}
