using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// Package R (E08, "columns first"): which table an alias typed before its FROM
/// stands for, and the FROM a column accepted there brings along.
/// </summary>
public class AliasGuessTests
{
    [Test]
    [Arguments("customers", "customers", 0)]
    [Arguments("c", "customers", 1)]
    [Arguments("oi", "order_items", 1)]
    [Arguments("tm", "team_members", 1)]
    [Arguments("ae", "audit_events", 1)]
    [Arguments("o2", "orders", 2)]
    [Arguments("inv", "invoices", 3)]
    [Arguments("pl", "plans", 3)]
    [Arguments("C", "customers", 1)]
    [Arguments("x", "customers", null)]
    [Arguments("oi", "orders", null)]
    [Arguments("o2x", "orders", null)]
    [Arguments("", "orders", null)]
    public async Task An_alias_fits_the_tables_people_shorten_that_way(string alias, string relation, int? fit)
    {
        await Assert.That(AliasGuess.Fit(alias, relation)).IsEqualTo(fit);
    }

    private static string Append(string marked, string insert, string clause)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        var edit = CompletionEdits.AppendClause(text, CompletionEdits.Plan(text, caret, insert, CompletionInsertKind.Plain), clause);
        var result = text.Remove(edit.ReplaceStart, edit.ReplaceLength).Insert(edit.ReplaceStart, edit.InsertText);
        return result.Insert(edit.CaretOffset, "|");
    }

    [Test]
    [Arguments("SELECT fir|", "SELECT first_name|\nFROM customers")]
    [Arguments("SELECT fir|, last_name", "SELECT first_name|, last_name\nFROM customers")]
    [Arguments("SELECT fir|  \n\n", "SELECT first_name|\nFROM customers  \n\n")]
    [Arguments("SELECT fir|;\nSELECT 2;", "SELECT first_name|\nFROM customers;\nSELECT 2;")]
    [Arguments("SELECT 1;\nSELECT fir|", "SELECT 1;\nSELECT first_name|\nFROM customers")]
    [Arguments("SELECT fir|\n\nSELECT 2", "SELECT first_name|\nFROM customers\n\nSELECT 2")]
    public async Task The_from_goes_at_the_end_of_the_statement_and_the_caret_stays(string marked, string expected)
    {
        await Assert.That(Append(marked, "first_name", "FROM customers")).IsEqualTo(expected);
    }

    [Test]
    public async Task A_document_with_crlf_gets_crlf()
    {
        await Assert.That(Append("SELECT fir|\r\n", "first_name", "FROM customers")).IsEqualTo("SELECT first_name|\r\nFROM customers\r\n");
    }
}
