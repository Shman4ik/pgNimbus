using PgNimbus.Core.Json;
using PgNimbus.Core.Tests.Text;

namespace PgNimbus.Core.Tests.Json;

public class JsonFoldingTests
{
    private const string Doc = "{\n  \"cc\": [\n    \"a@x.io\",\n    \"b@x.io\"\n  ],\n  \"one\": [1],\n  \"none\": {}\n}";

    [Test]
    public async Task Folds_every_multi_line_object_and_array_from_bracket_to_bracket()
    {
        var folds = JsonFolding.Find(Doc);

        await Assert.That(folds.Count).IsEqualTo(2);
        await Assert.That(Doc[folds[0].Start..folds[0].End]).IsEqualTo(Doc);
        await Assert.That(folds[0].IsObject).IsTrue();
        await Assert.That(folds[0].Count).IsEqualTo(3);
        await Assert.That(Doc[folds[1].Start..folds[1].End]).IsEqualTo("[\n    \"a@x.io\",\n    \"b@x.io\"\n  ]");
        await Assert.That(folds[1].Title).IsEqualTo("[ 2 items ]");
    }

    [Test]
    public async Task Brackets_and_commas_inside_strings_are_text()
    {
        const string json = "[\n  \"a, b ] { \\\" [\",\n  \"c\"\n]";

        var folds = JsonFolding.Find(json);

        await Assert.That(folds.Count).IsEqualTo(1);
        await Assert.That(folds[0].Count).IsEqualTo(2);
        await Assert.That(folds[0].End).IsEqualTo(json.Length);
    }

    [Test]
    public async Task An_empty_multi_line_container_counts_nothing()
    {
        var folds = JsonFolding.Find("{\n}");

        await Assert.That(folds.Single().Title).IsEqualTo("{ 0 fields }");
    }

    [Test]
    public async Task Text_being_edited_still_folds_where_its_brackets_match()
    {
        // The outer object never closes and the array's closing bracket is a
        // brace; the inner object is still a region.
        const string json = "{\n  \"a\": [\n    {\n      \"b\": 1\n    }\n  }\n";

        var folds = JsonFolding.Find(json);

        await Assert.That(folds.Count).IsEqualTo(1);
        await Assert.That(json[folds[0].Start..folds[0].End]).IsEqualTo("{\n      \"b\": 1\n    }");
    }

    [Test]
    public async Task A_missing_quote_ends_at_the_line_break()
    {
        const string json = "{\n  \"a\": \"unterminated,\n  \"b\": [\n    1\n  ]\n}";

        var folds = JsonFolding.Find(json);

        await Assert.That(folds.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Answers_on_any_text()
    {
        var read = 0;
        foreach (var text in HostileText.EveryCharAtEveryPosition().Concat(HostileText.Random(20260930, 2000)))
        {
            foreach (var fold in JsonFolding.Find(text))
            {
                if (fold.Start < 0 || fold.End > text.Length || fold.Start >= fold.End)
                {
                    throw new InvalidOperationException($"bad fold {fold} in {text}");
                }
            }

            read++;
        }

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    public async Task A_hundred_thousand_open_brackets_take_no_stack()
    {
        var text = string.Concat(Enumerable.Repeat("[\n", 100_000)) + string.Concat(Enumerable.Repeat("]\n", 100_000));
        var count = 0;
        HostileText.RunBounded(() => count = JsonFolding.Find(text).Count, TimeSpan.FromSeconds(30));

        await Assert.That(count).IsEqualTo(100_000);
    }
}
