using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

public class TableAliaserTests
{
    [Test]
    [Arguments("orders", "o")]
    [Arguments("order_items", "oi")]
    [Arguments("customer_order_items", "coi")]
    [Arguments("OrderItems", "oi")]
    [Arguments("Spells", "s")]
    public async Task Derive_TakesWordInitials(string table, string expected)
    {
        await Assert.That(TableAliaser.Derive(table, [])).IsEqualTo(expected);
    }

    [Test]
    public async Task Derive_DedupesWithNumericSuffix()
    {
        await Assert.That(TableAliaser.Derive("orders", ["o"])).IsEqualTo("o2");
        await Assert.That(TableAliaser.Derive("orders", ["o", "o2"])).IsEqualTo("o3");
    }

    [Test]
    public async Task Derive_TakenIsCaseInsensitive()
    {
        await Assert.That(TableAliaser.Derive("orders", ["O"])).IsEqualTo("o2");
    }

    [Test]
    public async Task Derive_SkipsReservedWords()
    {
        // "order_names" → "on" would misparse after a JOIN; go numbered.
        await Assert.That(TableAliaser.Derive("order_names", [])).IsEqualTo("on2");
        await Assert.That(TableAliaser.Derive("order_names", ["on2"])).IsEqualTo("on3");
    }

    [Test]
    public async Task Derive_NoLetters_FallsBackToT()
    {
        await Assert.That(TableAliaser.Derive("1234", [])).IsEqualTo("t");
        await Assert.That(TableAliaser.Derive("1234", ["t"])).IsEqualTo("t2");
    }

    [Test]
    public async Task Derive_DigitsAndPunctuationSplitWords()
    {
        // The letter after a non-letter starts a new word.
        await Assert.That(TableAliaser.Derive("audit2024_log", [])).IsEqualTo("al");
        await Assert.That(TableAliaser.Derive("user-events", [])).IsEqualTo("ue");
    }
}
