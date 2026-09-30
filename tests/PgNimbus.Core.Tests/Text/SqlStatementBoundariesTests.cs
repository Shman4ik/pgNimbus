using System.Text;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The editor's statement cache must answer exactly what
/// <see cref="SqlCompletionContext.CompletionStatementSpan"/> answers, on any text and
/// after any sequence of edits, since the per-keystroke readers are handed only the
/// statement it finds. Generated documents lean on everything that hides a ';':
/// strings, dollar quotes, quoted names, both comment forms, and ones left open.
/// </summary>
public class SqlStatementBoundariesTests
{
    private static readonly string[] Fragments =
    [
        "SELECT a, b FROM t", ";", " ", "\n", "'x;y'", "'open;", "$$ ; $$", "$tag$ ; $tag$", "$$ open ;",
        "\"na;me\"", "\"open ;", "-- c ; \n", "-- open ;", "/* ; */", "/* /* ; */ */", "/* open ;",
        "E'a\\';b'", "(", ")", "WHERE x = 1", "INSERT INTO t VALUES (1, ';')", "f(a;", "\r",
    ];

    private static string Document(Random random, int pieces)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pieces; i++)
        {
            builder.Append(Fragments[random.Next(Fragments.Length)]);
        }

        return builder.ToString();
    }

    [Test]
    public async Task Spans_match_the_full_lex_on_generated_documents()
    {
        var random = new Random(20260930);
        for (var run = 0; run < 300; run++)
        {
            var sql = Document(random, random.Next(1, 40));
            var boundaries = new SqlStatementBoundaries();
            for (var probe = 0; probe < 20; probe++)
            {
                var caret = random.Next(sql.Length + 1);
                var expected = SqlCompletionContext.CompletionStatementSpan(sql, caret);
                var actual = boundaries.Span(sql, caret);
                if (actual != expected)
                {
                    await Assert.That(actual).IsEqualTo(expected).Because($"caret {caret} in {sql}");
                }
            }
        }
    }

    [Test]
    public async Task Spans_stay_right_through_edits()
    {
        var random = new Random(930);
        for (var run = 0; run < 200; run++)
        {
            var sql = Document(random, random.Next(1, 30));
            var boundaries = new SqlStatementBoundaries();
            for (var step = 0; step < 25; step++)
            {
                // Read somewhere first, so the cache holds boundaries an edit has to drop.
                var caret = random.Next(sql.Length + 1);
                boundaries.Span(sql, caret);

                var offset = random.Next(sql.Length + 1);
                var removed = random.Next(Math.Min(6, sql.Length - offset) + 1);
                var inserted = random.Next(3) == 0 ? string.Empty : Fragments[random.Next(Fragments.Length)];
                sql = sql[..offset] + inserted + sql[(offset + removed)..];
                boundaries.Invalidate(offset);

                caret = random.Next(sql.Length + 1);
                var expected = SqlCompletionContext.CompletionStatementSpan(sql, caret);
                var actual = boundaries.Span(sql, caret);
                if (actual != expected)
                {
                    await Assert.That(actual).IsEqualTo(expected).Because($"caret {caret} in {sql}");
                }
            }
        }
    }

    [Test]
    public async Task The_caret_context_of_the_statement_alone_is_the_documents()
    {
        // What the editor relies on when it hands a reader one statement of a long
        // document: the clause and literal state at the caret don't depend on the
        // statements before it.
        var random = new Random(4242);
        var boundaries = new SqlStatementBoundaries();
        for (var run = 0; run < 300; run++)
        {
            var sql = Document(random, random.Next(1, 30));
            boundaries.Reset();
            var caret = random.Next(sql.Length + 1);
            var (start, end) = boundaries.Span(sql, caret);

            var whole = SqlCompletionContext.GetCaretContext(sql, caret);
            var statement = SqlCompletionContext.GetCaretContext(sql[start..end], caret - start);
            if (statement != whole)
            {
                await Assert.That(statement).IsEqualTo(whole).Because($"caret {caret} in {sql}");
            }
        }
    }

    [Test]
    public async Task A_long_document_is_not_lexed_again_for_a_caret_in_its_last_statement()
    {
        var sql = string.Concat(Enumerable.Repeat("SELECT 'a;b', \"c;d\" FROM t; ", 20_000)) + "SELECT x FROM ";
        var boundaries = new SqlStatementBoundaries();
        var first = boundaries.Span(sql, sql.Length);

        // Typing at the end: each edit invalidates only what follows it.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            sql += "y";
            boundaries.Invalidate(sql.Length - 1);
            boundaries.Span(sql, sql.Length);
        }

        watch.Stop();
        await Assert.That(boundaries.Span(sql, sql.Length).Start).IsEqualTo(first.Start);
        // Two hundred full lexes of this 600 KB document take seconds; two hundred
        // lexes of its last statement, a few milliseconds.
        await Assert.That(watch.ElapsedMilliseconds).IsLessThan(1000);
    }
}
