using PgNimbus.Core.Query;
using PgNimbus.Core.Text;
using TUnit.Assertions.Enums;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The shared lexer: each literal and comment form Postgres has, the
/// "unterminated is a state, not an error" contract, and the property every
/// consumer leans on — tokens tile the input exactly, on any text at all.
/// </summary>
public class SqlLexerTests
{
    private static List<(SqlTokenKind Kind, string Text)> Lex(string sql) =>
        [.. SqlLexer.Tokenize(sql).Where(t => t.Kind != SqlTokenKind.Whitespace).Select(t => (t.Kind, sql.Substring(t.Start, t.Length)))];

    [Test]
    [Arguments("E'can\\'t;stop'")]
    [Arguments("e'a\\\\'")]
    [Arguments("'it''s'")]
    [Arguments("N'national'")]
    [Arguments("B'1010'")]
    [Arguments("X'ff'")]
    [Arguments("U&'d\\0061t'")]
    public async Task String_forms_are_one_token(string literal)
    {
        var tokens = Lex(literal);

        await Assert.That(tokens).Count().IsEqualTo(1);
        await Assert.That(tokens[0].Kind).IsEqualTo(SqlTokenKind.String);
        await Assert.That(tokens[0].Text).IsEqualTo(literal);
    }

    [Test]
    public async Task A_backslash_is_ordinary_in_a_standard_string()
    {
        // standard_conforming_strings: 'a\' is a complete literal.
        var tokens = Lex("'a\\' x");

        await Assert.That(tokens[0]).IsEqualTo((SqlTokenKind.String, "'a\\'"));
        await Assert.That(tokens[1]).IsEqualTo((SqlTokenKind.Word, "x"));
    }

    [Test]
    public async Task Non_standard_strings_take_backslash_escapes_when_asked()
    {
        var tokens = SqlLexer.Tokenize("'a\\' x'", standardConformingStrings: false);

        await Assert.That(tokens).Count().IsEqualTo(1);
        await Assert.That(tokens[0].IsIncomplete).IsFalse();
    }

    [Test]
    [Arguments("$$a;b$$")]
    [Arguments("$tag$a;b$tag$")]
    [Arguments("$tag1$a;b$tag1$")]
    [Arguments("$тег$a;b$тег$")]
    [Arguments("$_x$ $not$ closed $_x$")]
    public async Task Dollar_quotes_include_tags_with_digits_and_unicode(string literal)
    {
        var tokens = Lex(literal);

        await Assert.That(tokens).Count().IsEqualTo(1);
        await Assert.That(tokens[0].Kind).IsEqualTo(SqlTokenKind.DollarString);
    }

    [Test]
    public async Task Dollar_digit_is_a_parameter_not_a_quote()
    {
        await Assert.That(Lex("$1 + $12")).IsEquivalentTo(new[]
        {
            (SqlTokenKind.Parameter, "$1"), (SqlTokenKind.Operator, "+"), (SqlTokenKind.Parameter, "$12"),
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Block_comments_nest()
    {
        var tokens = Lex("/* a /* b */ c */ x");

        await Assert.That(tokens[0]).IsEqualTo((SqlTokenKind.BlockComment, "/* a /* b */ c */"));
        await Assert.That(tokens[1]).IsEqualTo((SqlTokenKind.Word, "x"));
    }

    [Test]
    public async Task Quoted_identifiers_keep_doubled_quotes_and_unicode_escapes()
    {
        var tokens = Lex("\"a\"\"b\" U&\"x\"");

        await Assert.That(tokens[0]).IsEqualTo((SqlTokenKind.QuotedIdentifier, "\"a\"\"b\""));
        await Assert.That(tokens[1]).IsEqualTo((SqlTokenKind.QuotedIdentifier, "U&\"x\""));
        await Assert.That(SqlLexer.IdentifierName("\"a\"\"b\"", SqlLexer.Tokenize("\"a\"\"b\"")[0])).IsEqualTo("a\"b");
    }

    [Test]
    public async Task Punctuation_and_the_cast_operator()
    {
        await Assert.That(Lex("a.b::int[1],(x);")).IsEquivalentTo(new[]
        {
            (SqlTokenKind.Word, "a"), (SqlTokenKind.Dot, "."), (SqlTokenKind.Word, "b"),
            (SqlTokenKind.DoubleColon, "::"), (SqlTokenKind.Word, "int"), (SqlTokenKind.OpenBracket, "["),
            (SqlTokenKind.Number, "1"), (SqlTokenKind.CloseBracket, "]"), (SqlTokenKind.Comma, ","),
            (SqlTokenKind.OpenParen, "("), (SqlTokenKind.Word, "x"), (SqlTokenKind.CloseParen, ")"),
            (SqlTokenKind.Semicolon, ";"),
        }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("'open", SqlTokenKind.String)]
    [Arguments("E'open\\'", SqlTokenKind.String)]
    [Arguments("\"open", SqlTokenKind.QuotedIdentifier)]
    [Arguments("$t$open", SqlTokenKind.DollarString)]
    [Arguments("/* open /* */", SqlTokenKind.BlockComment)]
    public async Task Unterminated_tokens_are_flagged_not_thrown(string sql, SqlTokenKind kind)
    {
        var tokens = SqlLexer.Tokenize(sql);

        await Assert.That(tokens).Count().IsEqualTo(1);
        await Assert.That(tokens[0].Kind).IsEqualTo(kind);
        await Assert.That(tokens[0].IsIncomplete).IsTrue();
    }

    [Test]
    public async Task Unquoted_names_fold_ascii_only()
    {
        await Assert.That(SqlLexer.FoldCase("Users")).IsEqualTo("users");
        // The server leaves non-ASCII alone in UTF-8 databases.
        await Assert.That(SqlLexer.FoldCase("Ärger")).IsEqualTo("Ärger");
    }

    // Every printable ASCII character plus the ones the lexer treats
    // specially, in every position of a few shapes: whatever the text, the
    // tokens must advance and tile it exactly — the lexer runs on the UI thread
    // per keystroke, and a zero-width token would hang the app.
    [Test]
    public async Task Any_text_tokenizes_into_an_exact_tiling()
    {
        var alphabet = Enumerable.Range(32, 95).Select(c => (char)c).Concat("\n\tЖ€ ").ToArray();
        string[] shapes = ["SELECT E'a\\'b' FROM t -- c\n", "$t$x$t$ /* /* */ */ \"q\"\"q\" $1::int;", ""];

        foreach (var shape in shapes)
        {
            for (var at = 0; at <= shape.Length; at++)
            {
                foreach (var c in alphabet)
                {
                    var sql = shape.Insert(at, c.ToString());
                    AssertTiles(sql, SqlLexer.Tokenize(sql));
                }
            }
        }

        var random = new Random(20260922);
        for (var n = 0; n < 2000; n++)
        {
            var chars = new char[random.Next(0, 40)];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            var sql = new string(chars);
            AssertTiles(sql, SqlLexer.Tokenize(sql));
            // Sub-ranges keep their offsets in the whole text.
            var start = random.Next(0, sql.Length + 1);
            var end = random.Next(start, sql.Length + 1);
            var part = SqlLexer.Tokenize(sql, start, end);
            await Assert.That(part.Count == 0 ? start == end : part[0].Start == start && part[^1].End == end).IsTrue();
        }

        static void AssertTiles(string sql, List<SqlToken> tokens)
        {
            var at = 0;
            foreach (var token in tokens)
            {
                if (token.Start != at || token.Length <= 0)
                {
                    throw new InvalidOperationException($"bad tiling at {at} in {sql}");
                }

                at = token.End;
            }

            if (at != sql.Length)
            {
                throw new InvalidOperationException($"tokens stop at {at} of {sql.Length} in {sql}");
            }
        }
    }

    // --- The splitter rides the lexer ---

    [Test]
    public async Task Splitter_does_not_split_inside_an_escape_string()
    {
        var statements = SqlScriptSplitter.Split("SELECT E'can\\'t;stop'; SELECT 2;");

        await Assert.That(statements).IsEquivalentTo(new[] { "SELECT E'can\\'t;stop'", "SELECT 2" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Splitter_does_not_split_inside_tagged_dollar_strings()
    {
        var statements = SqlScriptSplitter.Split("SELECT $tag1$a;b$tag1$; SELECT $тег$c;d$тег$");

        await Assert.That(statements).IsEquivalentTo(new[] { "SELECT $tag1$a;b$tag1$", "SELECT $тег$c;d$тег$" }, CollectionOrdering.Matching);
    }
}
