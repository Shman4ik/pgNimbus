using System.Text;
using PgNimbus.Core.Localization;

namespace PgNimbus.Core.Tests.Localization;

/// <summary>
/// The string table: which language a system asks for, how placeholders and plurals
/// are filled, and that a translation falls back to English key by key. Tests that
/// switch the process-wide language are <c>[NotInParallel]</c> and put English back.
/// </summary>
public class LocalizerTests
{
    [Test]
    [Arguments("zh-CN", "zh-Hans")]
    [Arguments("zh-SG", "zh-Hans")]
    [Arguments("zh-Hans-CN", "zh-Hans")]
    [Arguments("zh_CN.UTF-8", "zh-Hans")]
    [Arguments("zh-TW", null)]
    [Arguments("zh-Hant-HK", null)]
    [Arguments("pt-BR", "pt-BR")]
    [Arguments("pt-PT", "pt-BR")]
    [Arguments("pt", "pt-BR")]
    [Arguments("de_AT.UTF-8@euro", "de")]
    [Arguments("de", "de")]
    [Arguments("C", null)]
    [Arguments("POSIX", null)]
    [Arguments("fr-FR", null)]
    public async Task A_system_tag_matches_the_closest_carried_language(string requested, string? expected)
    {
        string[] carried = ["en", "de", "pt-BR", "zh-Hans"];
        await Assert.That(Localizer.Match(requested, carried)).IsEqualTo(expected);
    }

    [Test]
    public async Task Auto_takes_the_first_system_language_carried_and_a_tag_is_taken_as_set()
    {
        string[] carried = ["en", "zh-Hans"];
        await Assert.That(Localizer.Resolve("auto", ["fr-FR", "zh-CN", "en-US"], carried)).IsEqualTo("zh-Hans");
        await Assert.That(Localizer.Resolve("auto", ["fr-FR"], carried)).IsEqualTo("en");
        await Assert.That(Localizer.Resolve("auto", [], carried)).IsEqualTo("en");
        await Assert.That(Localizer.Resolve(null, ["zh-CN"], carried)).IsEqualTo("zh-Hans");
        await Assert.That(Localizer.Resolve("en", ["zh-CN"], carried)).IsEqualTo("en");
        await Assert.That(Localizer.Resolve("xx", ["zh-CN"], carried)).IsEqualTo("en");
    }

    [Test]
    public async Task Placeholders_are_filled_by_name_in_any_order()
    {
        string[] names = ["count", "table"];
        await Assert.That(Localizer.Format("{count} rows from {table}", names, 3, "orders")).IsEqualTo("3 rows from orders");
        await Assert.That(Localizer.Format("从 {table} 删除了 {count} 行", names, 3, "orders")).IsEqualTo("从 orders 删除了 3 行");
        await Assert.That(Localizer.Format("{count} at {other} {", names, 1.5, "x")).IsEqualTo("1.5 at {other} {");
        await Assert.That(Localizer.Format("no placeholders", names, 1, "x")).IsEqualTo("no placeholders");
    }

    [Test]
    [Arguments("en", 1, "one")]
    [Arguments("en", 0, "other")]
    [Arguments("en", 2, "other")]
    [Arguments("zh-Hans", 1, "other")]
    [Arguments("fr", 0, "one")]
    [Arguments("ru", 1, "one")]
    [Arguments("ru", 11, "many")]
    [Arguments("ru", 22, "few")]
    [Arguments("ru", 25, "many")]
    [Arguments("ru", 101, "one")]
    [Arguments("pl", 1, "one")]
    [Arguments("pl", 21, "many")]
    [Arguments("pl", 23, "few")]
    [Arguments("cs", 3, "few")]
    [Arguments("cs", 5, "other")]
    public async Task Plural_categories_follow_CLDR(string language, long count, string expected) =>
        await Assert.That(PluralRules.Category(language, count)).IsEqualTo(expected);

    [Test]
    public async Task The_pseudo_locale_accents_text_and_keeps_placeholders()
    {
        await Assert.That(Localizer.Pseudolocalize("Save {name} as…")).IsEqualTo("[Šávé {name} áš…]");
    }

    [Test]
    public async Task A_resx_file_is_read_without_resolving_anything()
    {
        const string resx = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
              <data name="A" xml:space="preserve"><value>  spaced  </value><comment>note</comment></data>
              <data name="Icon" type="System.Drawing.Bitmap, System.Drawing"><value>AAAA</value></data>
              <data name="B"><value>二</value></data>
            </root>
            """;
        var table = Localizer.Read(new MemoryStream(Encoding.UTF8.GetBytes(resx)));
        await Assert.That(table.Count).IsEqualTo(2);
        await Assert.That(table["A"]).IsEqualTo("  spaced  ");
        await Assert.That(table["B"]).IsEqualTo("二");
    }

    [Test]
    public async Task A_resx_with_a_DTD_is_refused()
    {
        const string resx = """
            <?xml version="1.0"?>
            <!DOCTYPE root [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <root><data name="A"><value>&x;</value></data></root>
            """;
        await Assert.That(() => Localizer.Read(new MemoryStream(Encoding.UTF8.GetBytes(resx))))
            .Throws<System.Xml.XmlException>();
    }

    [Test]
    [NotInParallel]
    public async Task A_translation_falls_back_to_English_key_by_key_and_plurals_use_its_rules()
    {
        try
        {
            var seen = new List<string>();
            using var subscription = Localizer.Observe(StringKey.Settings_Language).Subscribe(new Collect(seen));

            Localizer.LoadTable("ru", new Dictionary<string, string>
            {
                ["Settings_Language"] = "Язык",
                ["Test_Rows_one"] = "{count} строка",
                ["Test_Rows_few"] = "{count} строки",
                ["Test_Rows_many"] = "{count} строк",
            });

            await Assert.That(Strings.Settings_Language).IsEqualTo("Язык");
            await Assert.That(Strings.Settings_LanguageSection).IsEqualTo("LANGUAGE");
            await Assert.That(Localizer.Plural("Test_Rows", 1, "{count} row", "{count} rows")).IsEqualTo("{count} строка");
            await Assert.That(Localizer.Plural("Test_Rows", 3, "{count} row", "{count} rows")).IsEqualTo("{count} строки");
            await Assert.That(Localizer.Plural("Test_Rows", 5, "{count} row", "{count} rows")).IsEqualTo("{count} строк");
            await Assert.That(Localizer.Plural("Test_Other", 5, "{count} item", "{count} items")).IsEqualTo("{count} items");

            Localizer.Load(Localizer.English);
            await Assert.That(seen).IsEquivalentTo(new[] { "Language", "Язык", "Language" });
        }
        finally
        {
            Localizer.Load(Localizer.English);
        }
    }

    private sealed class Collect(List<string> seen) : IObserver<string>
    {
        public void OnNext(string value) => seen.Add(value);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
