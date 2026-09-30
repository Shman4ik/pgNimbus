using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>Package Q's Core halves: keyword case (F02), matched letters (G01), argument hints (H01, H02).</summary>
public class CompletionDisplayTests
{
    [Test]
    [Arguments("TRUE", "tr", KeywordCase.AsTyped, "true")]
    [Arguments("ORDER BY", "ob", KeywordCase.AsTyped, "order by")]
    [Arguments("SELECT", "SEL", KeywordCase.AsTyped, "SELECT")]
    [Arguments("SELECT", "Sel", KeywordCase.AsTyped, "SELECT")]
    [Arguments("SELECT", "", KeywordCase.AsTyped, "SELECT")]
    [Arguments("FILTER (WHERE )", "f", KeywordCase.AsTyped, "filter (where )")]
    [Arguments("SELECT", "sel", KeywordCase.Upper, "SELECT")]
    [Arguments("SELECT", "SEL", KeywordCase.Lower, "select")]
    public async Task A_keyword_is_written_in_the_case_asked_for(string keyword, string typed, KeywordCase mode, string expected)
    {
        await Assert.That(KeywordCasing.Apply(keyword, typed, mode)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("email", "em", "0,1")] // prefix
    [Arguments("order_items", "oi", "0,6")] // the starts of its parts
    [Arguments("order_items", "ordit", "0,1,2,6,7")]
    [Arguments("customer_id", "tom", "3,4,5")] // a substring
    [Arguments("created_at", "cat", "0,8,9")] // part starts: c, at
    [Arguments("error_message", "ems", "0,6,8")] // anything else: the leftmost subsequence
    [Arguments("email", "xyz", "")]
    [Arguments("email", "", "")]
    public async Task The_matched_letters_are_the_ones_the_tier_read(string name, string query, string expected)
    {
        var positions = string.Join(',', CompletionRanker.MatchedPositions(name, query));

        await Assert.That(positions).IsEqualTo(expected);
    }

    [Test]
    public async Task Every_query_that_matches_marks_one_position_per_typed_letter()
    {
        var random = new Random(11);
        const string alphabet = "ab_cAB";
        for (var n = 0; n < 5000; n++)
        {
            var name = new string([.. Enumerable.Range(0, random.Next(1, 12)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            var query = new string([.. Enumerable.Range(0, random.Next(1, 4)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            if (FuzzyMatcher.Score(name, query) is null)
            {
                continue;
            }

            var positions = CompletionRanker.MatchedPositions(name, query);
            await Assert.That(positions.Count).IsEqualTo(query.Length);
            for (var i = 0; i < positions.Count; i++)
            {
                await Assert.That(char.ToLowerInvariant(name[positions[i]])).IsEqualTo(char.ToLowerInvariant(query[i]));
                if (i > 0)
                {
                    await Assert.That(positions[i]).IsGreaterThan(positions[i - 1]);
                }
            }
        }
    }

    private static SqlCallSite Site(string marked)
    {
        var caret = marked.IndexOf('|');
        return SqlCallSite.At(marked.Remove(caret, 1), caret)!;
    }

    [Test]
    [Arguments("SELECT extract(|", "field FROM source")]
    [Arguments("SELECT position(|", "substring IN string")]
    [Arguments("SELECT substring(|", "string FROM start FOR count")]
    [Arguments("SELECT trim(|", "[LEADING | TRAILING | BOTH] [characters] FROM string")]
    [Arguments("SELECT overlay(|", "string PLACING replacement FROM start FOR count")]
    public async Task Sqls_own_call_forms_are_hinted_as_written(string marked, string form)
    {
        var hints = SignatureHints.SpecialForms(Site(marked));

        await Assert.That(hints[0].Parameters.Single().Text).IsEqualTo(form);
        await Assert.That(hints[0].ActiveParameter).IsEqualTo(0);
    }

    [Test]
    public async Task Coalesce_and_nullif_are_hinted_though_pg_proc_has_neither()
    {
        var coalesce = SignatureHints.SpecialForms(Site("SELECT coalesce(a, b, |")).Single();
        var nullif = SignatureHints.SpecialForms(Site("SELECT nullif(a, |")).Single();

        await Assert.That(coalesce.ActiveParameter).IsEqualTo(1); // the variadic tail
        await Assert.That(nullif.ActiveParameter).IsEqualTo(1);
        await Assert.That(SignatureHints.SpecialForms(Site("SELECT lower(|"))).IsEmpty();
    }

    [Test]
    public async Task Ordinary_overloads_come_before_polymorphic_ones()
    {
        var hints = SignatureHints.For(Site("SELECT upper(|"),
        [
            ("pg_catalog", new FunctionInfo("upper", "anymultirange", "anyelement", 'f')),
            ("pg_catalog", new FunctionInfo("upper", "anyrange", "anyelement", 'f')),
            ("pg_catalog", new FunctionInfo("upper", "text", "text", 'f')),
        ]);

        await Assert.That(hints[0].Parameters[0].Text).IsEqualTo("text");
    }
}
