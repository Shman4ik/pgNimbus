using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Query;

public class BrowseSqlParserTests
{
    private static readonly IReadOnlyList<ColumnDetail> Columns =
    [
        new("id", "uuid", NotNull: true, IsPrimaryKey: true),
        new("full_name", "text", NotNull: true, IsPrimaryKey: false),
        new("order_count", "bigint", NotNull: false, IsPrimaryKey: false),
        new("lifetime_value", "numeric", NotNull: false, IsPrimaryKey: false),
        new("status", "order_status", NotNull: false, IsPrimaryKey: false) { Editor = ColumnValueEditor.Enum, EnumLabels = ["a", "b"] },
        new("active", "boolean", NotNull: false, IsPrimaryKey: false) { Editor = ColumnValueEditor.Boolean },
        new("payload", "json", NotNull: false, IsPrimaryKey: false) { Editor = ColumnValueEditor.Json },
    ];

    private static BrowseQueryShape? Parse(string sql) =>
        BrowseSqlParser.TryParse(sql, "analytics", "v_customer_spend", Columns);

    [Test]
    public async Task A_hand_added_where_becomes_a_typed_condition()
    {
        var shape = Parse("""
            SELECT *
              FROM "analytics"."v_customer_spend"
             WHERE order_count > 6
             LIMIT 100 OFFSET 0
            """);

        await Assert.That(shape).IsNotNull();
        await Assert.That(shape!.Conditions).Count().IsEqualTo(1);
        await Assert.That(shape.Conditions[0].Filter).IsEqualTo(new RowFilter("order_count", FilterOperator.Greater, "6"));
        await Assert.That(shape.Limit).IsEqualTo(100);
        await Assert.That(shape.Offset).IsEqualTo(0);
    }

    [Test]
    public async Task What_browse_mode_itself_writes_round_trips()
    {
        var predicates = new[]
        {
            RowFilterSql.ToPredicate(new RowFilter("full_name", FilterOperator.Contains, "O'Br_50%"), ColumnValueEditor.Text, "text"),
            RowFilterSql.ToPredicate(new RowFilter("order_count", FilterOperator.LessOrEqual, "4"), ColumnValueEditor.Text, "bigint"),
            RowFilterSql.ToPredicate(new RowFilter("status", FilterOperator.NotEquals, "b"), ColumnValueEditor.Enum, "order_status"),
            RowFilterSql.ToPredicate(new RowFilter("active", FilterOperator.IsFalse), ColumnValueEditor.Boolean, "boolean"),
            RowFilterSql.ToPredicate(new RowFilter("payload", FilterOperator.NotContains, "x"), ColumnValueEditor.Json, "json"),
            RowFilterSql.ToPredicate(new RowFilter("lifetime_value", FilterOperator.IsNotNull), ColumnValueEditor.Text, "numeric"),
        };
        var sql = $"SELECT * FROM \"analytics\".\"v_customer_spend\"\nWHERE {RowFilterSql.Combine(predicates)}\nORDER BY \"order_count\" DESC\nLIMIT 100 OFFSET 200";

        var shape = Parse(sql)!;

        await Assert.That(shape.Conditions.Select(c => c.Filter)).IsEquivalentTo(new RowFilter?[]
        {
            new("full_name", FilterOperator.Contains, "O'Br_50%"),
            new("order_count", FilterOperator.LessOrEqual, "4"),
            new("status", FilterOperator.NotEquals, "b"),
            new("active", FilterOperator.IsFalse),
            new("payload", FilterOperator.NotContains, "x"),
            new("lifetime_value", FilterOperator.IsNotNull),
        });
        await Assert.That(shape.SortColumn).IsEqualTo("order_count");
        await Assert.That(shape.SortDescending).IsTrue();
        await Assert.That(shape.Offset).IsEqualTo(200);
    }

    [Test]
    public async Task Anything_else_in_the_where_is_kept_verbatim_as_a_raw_condition()
    {
        var shape = Parse("""
            SELECT * FROM v_customer_spend
            WHERE (order_count > 6 OR full_name ILIKE 'a%')
              AND lifetime_value BETWEEN 10 AND 20
              AND lower(full_name) = 'x'
              AND id IN (SELECT id FROM other)
              AND order_count >= -3
            LIMIT 50
            """)!;

        await Assert.That(shape.Conditions.Select(c => c.Text)).IsEquivalentTo(new[]
        {
            "order_count > 6 OR full_name ILIKE 'a%'",
            "lifetime_value BETWEEN 10 AND 20",
            "lower(full_name) = 'x'",
            "id IN (SELECT id FROM other)",
            "order_count >= -3",
        });
        await Assert.That(shape.Conditions.Take(4).All(c => c.Filter is null)).IsTrue();
        await Assert.That(shape.Conditions[4].Filter).IsEqualTo(new RowFilter("order_count", FilterOperator.GreaterOrEqual, "-3"));
        await Assert.That(shape.Limit).IsEqualTo(50);
    }

    [Test]
    public async Task A_comparison_the_chip_cannot_show_stays_raw()
    {
        // json has no "=", so no chip could offer it — keep the text.
        var shape = Parse("SELECT * FROM v_customer_spend WHERE payload = '{}' AND full_name LIKE 'A%' LIMIT 10")!;

        await Assert.That(shape.Conditions.All(c => c.Filter is null)).IsTrue();
    }

    [Test]
    public async Task Patterns_with_inner_wildcards_are_not_mistaken_for_text_searches()
    {
        var shape = Parse("SELECT * FROM v_customer_spend WHERE full_name ILIKE '%a%b%' AND full_name ILIKE 'a_b%' LIMIT 10")!;

        await Assert.That(shape.Conditions.All(c => c.Filter is null)).IsTrue();
    }

    [Test]
    [Arguments("SELECT id FROM v_customer_spend LIMIT 10")]
    [Arguments("SELECT * FROM v_customer_spend")]
    [Arguments("SELECT * FROM other_table LIMIT 10")]
    [Arguments("SELECT * FROM public.v_customer_spend LIMIT 10")]
    [Arguments("SELECT * FROM v_customer_spend WHERE order_count > 1 GROUP BY 1 LIMIT 10")]
    [Arguments("SELECT * FROM v_customer_spend ORDER BY full_name, order_count LIMIT 10")]
    [Arguments("SELECT * FROM v_customer_spend LIMIT 10; DELETE FROM v_customer_spend")]
    [Arguments("SELECT * FROM v_customer_spend JOIN x ON true LIMIT 10")]
    [Arguments("SELECT * FROM v_customer_spend WHERE full_name = 'unterminated LIMIT 10")]
    public async Task A_query_outside_the_browse_shape_is_not_a_browse_query(string sql)
    {
        await Assert.That(Parse(sql)).IsNull();
    }

    [Test]
    public async Task Comments_semicolons_and_primary_key_order_are_fine()
    {
        var shape = Parse("""
            -- my filter
            SELECT * FROM "v_customer_spend" /* the view */
            WHERE "full_name" = 'Ann' -- trailing
            ORDER BY id
            LIMIT 100;
            """);

        await Assert.That(shape).IsNotNull();
        await Assert.That(shape!.SortColumn).IsNull();
        await Assert.That(shape.Conditions[0].Filter).IsEqualTo(new RowFilter("full_name", FilterOperator.Equals, "Ann"));
    }

    [Test]
    public async Task Any_text_at_all_finishes_parsing()
    {
        // The parser runs on the UI thread when a browse tab's edited query is
        // run, so a tokenizer that stops advancing hangs the app. It did: a lone
        // ':' (from "Warcraft 3: Forsaken Kingdom" pasted into the WHERE) made a
        // zero-width token forever. Every printable ASCII character, in every
        // position a user might leave it, must still come back.
        var inputs = new List<string>
        {
            "SELECT * FROM v_customer_spend\nWarcraft 3: Forsaken Kingdom\nORDER BY id\nLIMIT 100 OFFSET 0",
            "SELECT * FROM v_customer_spend WHERE a := 1 LIMIT 1",
            "SELECT * FROM v_customer_spend WHERE $ LIMIT 1",
            "SELECT * FROM v_customer_spend WHERE $1 = $$x LIMIT 1",
            "SELECT * FROM v_customer_spend WHERE x = E'\\",
            "SELECT * FROM v_customer_spend WHERE /* never closed",
            "SELECT * FROM v_customer_spend WHERE -",
            ":", "::", "$", "'", "\"", "/*", "--", "E'", ".", "",
        };
        for (var c = (char)32; c < 127; c++)
        {
            inputs.Add($"SELECT * FROM v_customer_spend WHERE full_name {c} 'x' LIMIT 10");
            inputs.Add($"SELECT * FROM v_customer_spend WHERE {c}");
            inputs.Add(c.ToString());
        }

        var parsing = Task.Run(() =>
        {
            foreach (var sql in inputs)
            {
                _ = Parse(sql);
            }
        });

        await Assert.That(parsing.Wait(TimeSpan.FromSeconds(10))).IsTrue();
    }

    // --- On the shared SqlLexer: the literal forms it reads, and never guesses at ---

    [Test]
    public async Task A_nested_comment_is_dropped_whole()
    {
        // The old scanner closed the comment at the first "*/" and kept
        // "c */ order_count = 1" as a raw condition, which would be ANDed back
        // into the query as broken SQL.
        var shape = Parse("SELECT * FROM analytics.v_customer_spend WHERE /* a /* b */ c */ order_count = 1 LIMIT 100");

        await Assert.That(shape!.Conditions.Single().Filter).IsEqualTo(new RowFilter("order_count", FilterOperator.Equals, "1"));
    }

    [Test]
    [Arguments(@"full_name = E'a\'b'")]
    [Arguments("full_name = $t1$x;y$t1$")]
    [Arguments("full_name = $тег$x$тег$")]
    [Arguments("full_name = N'x'")]
    [Arguments("full_name = U&'x'")]
    [Arguments("U&\"full_name\" = 'x'")]
    [Arguments("order_count = 0x1F")]
    [Arguments("id = $1")]
    public async Task A_literal_the_chips_cannot_reproduce_stays_a_raw_condition(string condition)
    {
        var shape = Parse($"SELECT * FROM analytics.v_customer_spend WHERE {condition} LIMIT 100");

        await Assert.That(shape).IsNotNull();
        await Assert.That(shape!.Conditions.Single()).IsEqualTo(new ParsedCondition(condition, null));
    }

    [Test]
    [Arguments("SELECT * FROM analytics.v_customer_spend WHERE full_name = 'open LIMIT 100")]
    [Arguments("SELECT * FROM analytics.v_customer_spend WHERE \"open = 'x' LIMIT 100")]
    [Arguments("SELECT * FROM analytics.v_customer_spend WHERE a = 1 /* open LIMIT 100")]
    [Arguments("SELECT * FROM analytics.v_customer_spend WHERE a = $x$open LIMIT 100")]
    [Arguments("SELECT * FROM analytics.v_customer_spend LIMIT 0x10")]
    public async Task Unfinished_or_unreadable_text_is_not_a_browse_query(string sql)
    {
        await Assert.That(Parse(sql)).IsNull();
    }
}
