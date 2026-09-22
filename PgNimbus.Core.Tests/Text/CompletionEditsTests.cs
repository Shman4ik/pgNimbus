using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// What accepting a suggestion writes (<see cref="CompletionEdits"/>): the
/// range it replaces, the parens of a callable, the auto-alias, and where the
/// caret lands. <c>|</c> marks the caret; each case applies the edit and
/// checks the resulting text, caret included.
/// </summary>
public class CompletionEditsTests
{
    private static string Accept(string marked, string insert, CompletionInsertKind kind = CompletionInsertKind.Plain, string? aliasSeed = null)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        var edit = CompletionEdits.Plan(text, caret, insert, kind, aliasSeed);
        var result = text.Remove(edit.ReplaceStart, edit.ReplaceLength).Insert(edit.ReplaceStart, edit.InsertText);
        return result.Insert(edit.CaretOffset, "|");
    }

    // --- T28: the token, not just what's left of the caret ---

    [Test]
    public async Task The_prefix_before_the_caret_is_replaced()
    {
        await Assert.That(Accept("sel|", "SELECT")).IsEqualTo("SELECT|");
    }

    [Test]
    public async Task Accepting_mid_word_replaces_the_whole_word()
    {
        await Assert.That(Accept("SELECT cust|omer_id FROM t", "customer_id")).IsEqualTo("SELECT customer_id| FROM t");
    }

    [Test]
    public async Task The_filter_starts_at_the_word_not_the_caret()
    {
        var token = CompletionEdits.TokenAt("SELECT sel", 10);

        await Assert.That(token).IsEqualTo(new CompletionToken(7, 7, 10));
    }

    // --- T10 / T29: quoted identifiers ---

    [Test]
    public async Task A_quoted_identifier_filters_after_its_quote_and_is_replaced_whole()
    {
        var token = CompletionEdits.TokenAt("WHERE \"Or\"", 9);
        await Assert.That(token).IsEqualTo(new CompletionToken(7, 6, 10));

        await Assert.That(Accept("WHERE \"Or|\" = 1", "\"Order Id\"")).IsEqualTo("WHERE \"Order Id\"| = 1");
        await Assert.That(Accept("WHERE \"na|", "name")).IsEqualTo("WHERE name|");
    }

    // --- T29: callables ---

    [Test]
    public async Task A_function_lands_the_caret_between_its_parens()
    {
        await Assert.That(Accept("SELECT coa|", "coalesce()", CompletionInsertKind.Function)).IsEqualTo("SELECT coalesce(|)");
    }

    [Test]
    public async Task A_function_before_an_existing_paren_reuses_it()
    {
        await Assert.That(Accept("SELECT coa|(a, b)", "coalesce()", CompletionInsertKind.Function))
            .IsEqualTo("SELECT coalesce(|a, b)");
    }

    // --- T29 / T30: the auto-alias is part of the same edit ---

    [Test]
    public async Task A_table_in_from_gets_an_alias_in_the_same_edit()
    {
        await Assert.That(Accept("SELECT * FROM ord|", "public.orders", CompletionInsertKind.Table, "orders"))
            .IsEqualTo("SELECT * FROM public.orders o|");
    }

    [Test]
    public async Task An_alias_already_typed_is_kept()
    {
        await Assert.That(Accept("SELECT * FROM ord| x WHERE x.id = 1", "public.orders", CompletionInsertKind.Table, "orders"))
            .IsEqualTo("SELECT * FROM public.orders| x WHERE x.id = 1");
        await Assert.That(Accept("SELECT * FROM ord| AS x", "public.orders", CompletionInsertKind.Table, "orders"))
            .IsEqualTo("SELECT * FROM public.orders| AS x");
    }

    [Test]
    public async Task A_following_keyword_is_not_an_alias()
    {
        await Assert.That(Accept("SELECT * FROM ord| WHERE true", "public.orders", CompletionInsertKind.Table, "orders"))
            .IsEqualTo("SELECT * FROM public.orders o| WHERE true");
    }

    [Test]
    public async Task No_alias_outside_from_and_join()
    {
        await Assert.That(Accept("INSERT INTO ord|", "public.orders", CompletionInsertKind.Table, "orders"))
            .IsEqualTo("INSERT INTO public.orders|");
    }

    [Test]
    public async Task The_alias_avoids_names_of_this_statement_only()
    {
        // "o" is taken in this statement; the neighbour's "oi" is irrelevant.
        await Assert.That(Accept("SELECT 1 FROM x oi; SELECT * FROM owners o JOIN ord|", "public.orders", CompletionInsertKind.Table, "orders"))
            .IsEqualTo("SELECT 1 FROM x oi; SELECT * FROM owners o JOIN public.orders o2|");
    }
}
