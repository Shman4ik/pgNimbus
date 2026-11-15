using System.Text.RegularExpressions;
using PgNimbus.Core.Localization;

namespace PgNimbus.Core.Tests.Localization;

/// <summary>
/// Every translation file in <c>src/PgNimbus.Core/Localization</c> is checked
/// against the English base, so a stale or broken translation fails here and not
/// on a user's screen: it parses, names its language, has no key the base lacks,
/// keeps every placeholder of the English text (and adds none), and uses only the
/// plural forms its language has. Missing keys are fine: they show in English.
/// </summary>
public partial class TranslationFileTests
{
    private static readonly string[] PluralForms = ["zero", "one", "two", "few", "many", "other"];

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();

    public static IEnumerable<string> Translations() =>
        Directory.GetFiles(LocalizationDirectory(), "Strings.*.resx").Select(Path.GetFileName)!;

    [Test]
    public async Task The_base_file_parses_and_every_key_is_generated()
    {
        var english = ReadFile("Strings.resx");
        await Assert.That(english.Count).IsGreaterThan(0);
        var keys = english.Keys.Where(k => !k.StartsWith('_')).Order(StringComparer.Ordinal).ToList();
        await Assert.That(keys).IsEquivalentTo(Strings.AllKeys.Order(StringComparer.Ordinal).ToList());
    }

    [Test]
    public async Task Every_translation_embedded_is_a_file_here()
    {
        var embedded = Localizer.Available.Select(l => l.Tag).Where(t => t != Localizer.English).Order().ToList();
        var files = Translations().Select(f => f!["Strings.".Length..^".resx".Length]).Order().ToList();
        await Assert.That(embedded).IsEquivalentTo(files);
    }

    /// <summary>One test over every file, so it still runs (and passes) with none.</summary>
    [Test]
    public async Task Every_translation_matches_the_base()
    {
        var problems = new List<string>();
        foreach (var file in Translations())
        {
            problems.AddRange(Check(file!).Select(p => file + ": " + p));
        }

        await Assert.That(problems).IsEmpty();
    }

    /// <summary>What is wrong with one translation file; empty when nothing is.</summary>
    public static List<string> Check(string file)
    {
        var english = ReadFile("Strings.resx");
        var translation = ReadFile(file);
        var tag = file["Strings.".Length..^".resx".Length];
        var problems = new List<string>();

        if (!translation.TryGetValue("_LanguageName", out var name) || string.IsNullOrWhiteSpace(name))
        {
            problems.Add("_LanguageName is missing: it is the language's own name, shown in Settings.");
        }

        var category = Enumerable.Range(0, 200).Select(n => PluralRules.Category(tag, n)).ToHashSet();
        foreach (var (key, text) in translation)
        {
            if (key.StartsWith('_'))
            {
                continue;
            }

            var form = PluralForms.FirstOrDefault(f => key.EndsWith("_" + f, StringComparison.Ordinal));
            var stem = form is null ? null : key[..^(form.Length + 1)];
            var isPlural = stem is not null && english.ContainsKey(stem + "_other") && english.ContainsKey(stem + "_one");

            string? source;
            if (isPlural)
            {
                if (!category.Contains(form!))
                {
                    problems.Add($"{key}: {tag} has no '{form}' plural form (it uses {string.Join(", ", category.Order())}).");
                }

                source = english[stem + "_other"];
            }
            else if (!english.TryGetValue(key, out source))
            {
                problems.Add($"{key}: not in Strings.resx (renamed or removed there?).");
                continue;
            }

            var expected = Names(source).Where(n => !isPlural || n != "count").ToHashSet();
            var actual = Names(text).Where(n => !isPlural || n != "count").ToHashSet();
            if (!expected.SetEquals(actual))
            {
                problems.Add($"{key}: placeholders {{{string.Join("}, {", actual)}}} differ from the English {{{string.Join("}, {", expected)}}}.");
            }
        }

        return problems;
    }

    private static HashSet<string> Names(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    private static Dictionary<string, string> ReadFile(string name)
    {
        using var stream = File.OpenRead(Path.Combine(LocalizationDirectory(), name));
        return Localizer.Read(stream);
    }

    private static string LocalizationDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PgNimbus.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "src", "PgNimbus.Core", "Localization");
    }
}
