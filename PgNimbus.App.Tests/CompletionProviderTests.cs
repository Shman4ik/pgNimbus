using PgNimbus.App.Completion;
using PgNimbus.Core.Schema;

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

        await Assert.That(condition.InsertText).IsEqualTo("o.user_id = u.id");
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
        var items = At(provider, "sel|");

        await Assert.That(items.Any(i => i.Text == "SELECT")).IsTrue();
        await Assert.That(items.Any(i => i.Text == "coalesce")).IsTrue();
    }
}
