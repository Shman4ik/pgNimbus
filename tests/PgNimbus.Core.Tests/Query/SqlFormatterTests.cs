using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// The formatter follows <see href="https://www.sqlstyle.guide/">sqlstyle.guide</see>:
/// root keywords right-align so they all end at the same column, forming a
/// whitespace "river" between keyword and content. Expected strings here spell
/// out that alignment exactly — the leading spaces are the point of the test.
/// </summary>
public class SqlFormatterTests
{
    private static async Task AssertFormats(string sql, string expected)
    {
        // The formatter always emits '\n'; normalize the expected raw string
        // literal, whose line endings follow this source file's.
        await Assert.That(SqlFormatter.Format(sql)).IsEqualTo(expected.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task CompactsTrivialBrowseQuery()
    {
        await AssertFormats(
            "SELECT * FROM \"games\".\"Books\" LIMIT 100 OFFSET 0",
            """
            SELECT *
              FROM "games"."Books"
             LIMIT 100 OFFSET 0
            """);
    }

    [Test]
    public async Task AlignsRootKeywordsToRiver()
    {
        // The album example from sqlstyle.guide, reproduced spacing-exactly.
        await AssertFormats(
            "SELECT a.title, a.release_date, a.recording_date FROM albums AS a "
            + "WHERE a.title = 'Charcoal Lane' OR a.title = 'The New Danger'",
            """
            SELECT a.title, a.release_date, a.recording_date
              FROM albums AS a
             WHERE a.title = 'Charcoal Lane'
                OR a.title = 'The New Danger'
            """);
    }

    [Test]
    public async Task GivesJoinsTheirOwnLineWithOnBeneath()
    {
        // The rider join example from sqlstyle.guide.
        await AssertFormats(
            "SELECT r.last_name FROM riders AS r INNER JOIN bikes AS b "
            + "ON r.bike_vin_num = b.vin_num AND b.engine_tally > 2 GROUP BY r.last_name",
            """
            SELECT r.last_name
              FROM riders AS r
             INNER JOIN bikes AS b
                   ON r.bike_vin_num = b.vin_num AND b.engine_tally > 2
             GROUP BY r.last_name
            """);
    }

    [Test]
    public async Task KeepsShortSelectListOnKeywordLine()
    {
        await AssertFormats(
            "select id, name from users where active = true limit 10",
            """
            SELECT id, name
              FROM users
             WHERE active = TRUE
             LIMIT 10
            """);
    }

    [Test]
    public async Task WrapsLongSelectListOneItemPerLine()
    {
        await AssertFormats(
            "select user_id, first_name, last_name, email_address, phone_number, "
            + "street_address, city, postal_code, country_code from customers",
            """
            SELECT user_id,
                   first_name,
                   last_name,
                   email_address,
                   phone_number,
                   street_address,
                   city,
                   postal_code,
                   country_code
              FROM customers
            """);
    }

    [Test]
    public async Task KeepsBetweenAndInline()
    {
        await AssertFormats(
            "select * from events where event_type = 'click' and occurred_at "
            + "between '2024-01-01' and '2024-12-31' and user_agent not ilike '%bot%'",
            """
            SELECT *
              FROM events
             WHERE event_type = 'click'
               AND occurred_at BETWEEN '2024-01-01' AND '2024-12-31'
               AND user_agent NOT ILIKE '%bot%'
            """);
    }

    [Test]
    public async Task NestsSubqueryRiverAtItsParen()
    {
        await AssertFormats(
            "select * from orders where user_id in (select id from users "
            + "where active = true and created_at > '2020-01-01') and total > 100",
            """
            SELECT *
              FROM orders
             WHERE user_id IN (SELECT id
                                 FROM users
                                WHERE active = TRUE
                                  AND created_at > '2020-01-01')
               AND total > 100
            """);
    }

    [Test]
    public async Task FormatsDerivedTable()
    {
        await AssertFormats(
            "select * from (select id, name from users where active) t where t.id > 100",
            """
            SELECT *
              FROM (SELECT id, name
                      FROM users
                     WHERE active) t
             WHERE t.id > 100
            """);
    }

    [Test]
    public async Task FormatsCte()
    {
        await AssertFormats(
            "with recent as (select * from orders where created_at > '2026-07-01') "
            + "select user_id, count(*) from recent group by user_id",
            """
            WITH recent AS (SELECT *
                              FROM orders
                             WHERE created_at > '2026-07-01')
            SELECT user_id, count(*)
              FROM recent
             GROUP BY user_id
            """);
    }

    [Test]
    public async Task LaysOutInsertValuesReturning()
    {
        await AssertFormats(
            "insert into users (name, email, active) values ('a', 'b@c.d', true), "
            + "('x', 'y@z.w', false) returning id",
            """
            INSERT INTO users (name, email, active)
            VALUES ('a', 'b@c.d', TRUE), ('x', 'y@z.w', FALSE)
            RETURNING id
            """);
    }

    [Test]
    public async Task LaysOutUpdateSet()
    {
        // The update example from sqlstyle.guide: SET right-aligns to the river.
        await AssertFormats(
            "update albums set release_date = '1990-01-01' where title = 'The New Danger'",
            """
            UPDATE albums
               SET release_date = '1990-01-01'
             WHERE title = 'The New Danger'
            """);
    }

    [Test]
    public async Task GluesDeleteFrom()
    {
        await AssertFormats(
            "delete from users where id = 5 returning id",
            """
            DELETE FROM users
             WHERE id = 5
            RETURNING id
            """);
    }

    [Test]
    public async Task UppercasesKeywordsButNotIdentifiers()
    {
        await AssertFormats(
            "select Id, FullName from Users",
            """
            SELECT Id, FullName
              FROM Users
            """);
    }

    [Test]
    public async Task NeverSwallowsLineCommentIntoFoldedLine()
    {
        await AssertFormats(
            "select id, -- primary key\n name from users",
            """
            SELECT id,
                   -- primary key
                   name
              FROM users
            """);
    }

    [Test]
    public async Task DoesNotPairPaginationAcrossLineComment()
    {
        await AssertFormats(
            "select * from t limit 10 -- ten\n offset 5",
            """
            SELECT *
              FROM t
             LIMIT 10 -- ten
            OFFSET 5
            """);
    }

    [Test]
    public async Task PreservesMultiLineBlockCommentVerbatim()
    {
        var formatted = SqlFormatter.Format("select id /* multi\nline */ , name from users");

        await Assert.That(formatted).Contains("/* multi\nline */");
    }

    [Test]
    public async Task SplitsMultipleStatementsOntoOwnLines()
    {
        await AssertFormats(
            "select 1; select 2;",
            """
            SELECT 1;
            SELECT 2;
            """);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("-- just a comment")]
    public async Task ReturnsDegenerateInputUnchanged(string sql)
    {
        await Assert.That(SqlFormatter.Format(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task LeavesDollarQuotedBodyUntouched()
    {
        var sql = "create function f() returns int as $$ select 1; $$ language sql";

        await Assert.That(SqlFormatter.Format(sql)).Contains("$$ select 1; $$");
    }

    [Test]
    [Arguments("SELECT * FROM \"games\".\"Books\" LIMIT 100 OFFSET 0")]
    [Arguments("select u.id, count(o.id) from users u left join orders o on o.user_id = u.id "
        + "where u.active group by u.id having count(o.id) > 5 order by 2 desc limit 50")]
    [Arguments("select * from orders where user_id in (select id from users where active) and total > 100")]
    [Arguments("with recent as (select * from orders) select * from recent")]
    [Arguments("insert into t (a, b) values (1, 2) returning a")]
    [Arguments("select id, -- pk\n name from users")]
    [Arguments("select id /* multi\nline */ , name from users")]
    public async Task FormattingIsIdempotent(string sql)
    {
        var once = SqlFormatter.Format(sql);

        await Assert.That(SqlFormatter.Format(once)).IsEqualTo(once);
    }
}
