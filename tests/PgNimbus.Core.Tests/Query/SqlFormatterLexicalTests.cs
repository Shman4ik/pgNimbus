using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

/// <summary>
/// Formatter examples for input where the lexer decides the meaning: escape
/// and dollar-quoted strings with a <c>;</c> inside, nested comments, prefixed
/// literals, multi-character operators, parameters, numbers. The expected
/// text is a literal, not a lexer's output: the formatter's own round-trip check
/// compares two runs of one tokenizer, so a tokenizer that misreads a literal
/// misreads it the same way on both sides and passes. These are the independent
/// half (docs/design/sql-editing-experience.md §7.4).
/// </summary>
public class SqlFormatterLexicalTests
{
    private static async Task AssertFormats(string sql, string expected) =>
        await Assert.That(SqlFormatter.Format(sql)).IsEqualTo(expected.ReplaceLineEndings("\n"));

    [Test]
    public async Task A_semicolon_inside_an_escape_string_stays_in_the_string()
    {
        await AssertFormats(
            "select E'can\\'t;stop' as a, 'x' from t where b = 1",
            """
            SELECT E'can\'t;stop' AS a, 'x'
              FROM t
             WHERE b = 1
            """);
    }

    [Test]
    public async Task Dollar_quotes_with_digits_and_non_ascii_tags_are_kept_whole()
    {
        await AssertFormats(
            "select $tag1$a;b$tag1$ as body, $тег$x$тег$ from t",
            """
            SELECT $tag1$a;b$tag1$ AS body, $тег$x$тег$
              FROM t
            """);
    }

    [Test]
    public async Task A_nested_block_comment_is_one_comment()
    {
        await AssertFormats(
            "select /* outer /* inner */ still */ a from t",
            """
            SELECT /* outer /* inner */ still */ a
              FROM t
            """);
    }

    [Test]
    public async Task Prefixed_literals_stay_glued_to_their_prefix()
    {
        // N'…' used to come out as "N 'national'" — a different expression.
        await AssertFormats(
            "select N'national', B'1010', X'ff', U&\"d\\0061t\" , U&'x' from t",
            """
            SELECT N'national', B'1010', X'ff', U&"d\0061t", U&'x'
              FROM t
            """);
    }

    [Test]
    public async Task Multi_character_operators_and_casts_stay_whole()
    {
        await AssertFormats(
            "select data->>'name', data #>> '{a,b}', x::int, y::numeric(10,2) from t where a <= 3 and b <> 4",
            """
            SELECT data ->> 'name', data #>> '{a,b}', x::int, y::numeric(10, 2)
              FROM t
             WHERE a <= 3
               AND b <> 4
            """);
    }

    [Test]
    public async Task Numbers_parameters_and_members_keep_their_meaning()
    {
        // 1_000 (PostgreSQL 16 digit separators) used to come out as "1 _000":
        // the number 1 aliased _000.
        await AssertFormats(
            "select 1_000, .5, 1.5e10, t.col, 3.14 from t where id = $1",
            """
            SELECT 1_000, .5, 1.5e10, t.col, 3.14
              FROM t
             WHERE id = $1
            """);
    }

    [Test]
    public async Task Unterminated_literals_are_left_alone_rather_than_reflowed_into()
    {
        const string sql = "select 'unterminated from t where x = 1";
        await Assert.That(SqlFormatter.Format(sql)).DoesNotContain("\n  FROM");
    }
}
