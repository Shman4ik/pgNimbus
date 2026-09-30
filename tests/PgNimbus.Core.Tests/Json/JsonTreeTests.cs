using PgNimbus.Core.Json;

namespace PgNimbus.Core.Tests.Json;

public class JsonTreeTests
{
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("{unclosed")]
    [Arguments("{\"a\": 1,}")] // trailing comma
    public async Task NonJsonReturnsNull(string text)
    {
        await Assert.That(JsonTree.Parse(text)).IsNull();
    }

    [Test]
    public async Task RootScalarIsALeaf()
    {
        var root = JsonTree.Parse("42");

        await Assert.That(root).IsNotNull();
        await Assert.That(root!.Name).IsEqualTo("$");
        await Assert.That(root.Kind).IsEqualTo(JsonNodeKind.Number);
        await Assert.That(root.ValuePreview).IsEqualTo("42");
        await Assert.That(root.HasChildren).IsFalse();
    }

    [Test]
    public async Task StringLeafIsQuoted()
    {
        var root = JsonTree.Parse("\"hi\"");

        await Assert.That(root!.Kind).IsEqualTo(JsonNodeKind.String);
        await Assert.That(root.ValuePreview).IsEqualTo("\"hi\"");
    }

    [Test]
    public async Task NullLeafHasNullKind()
    {
        var root = JsonTree.Parse("null");

        await Assert.That(root!.Kind).IsEqualTo(JsonNodeKind.Null);
        await Assert.That(root.ValuePreview).IsEqualTo("null");
    }

    [Test]
    public async Task ObjectMembersKeyedByPropertyName()
    {
        var root = JsonTree.Parse("""{"a": 1, "b": true, "c": null}""");

        await Assert.That(root!.Kind).IsEqualTo(JsonNodeKind.Object);
        await Assert.That(root.HasChildren).IsTrue();
        await Assert.That(root.Children.Count).IsEqualTo(3);
        await Assert.That(root.ValuePreview).IsEqualTo("{ 3 fields }");

        await Assert.That(root.Children[0].Name).IsEqualTo("a");
        await Assert.That(root.Children[0].Kind).IsEqualTo(JsonNodeKind.Number);
        await Assert.That(root.Children[1].Name).IsEqualTo("b");
        await Assert.That(root.Children[1].Kind).IsEqualTo(JsonNodeKind.Boolean);
        await Assert.That(root.Children[2].Name).IsEqualTo("c");
        await Assert.That(root.Children[2].Kind).IsEqualTo(JsonNodeKind.Null);
    }

    [Test]
    public async Task ArrayElementsNamedByIndex()
    {
        var root = JsonTree.Parse("""["x", "y", "z"]""");

        await Assert.That(root!.Kind).IsEqualTo(JsonNodeKind.Array);
        await Assert.That(root.ValuePreview).IsEqualTo("[ 3 items ]");
        await Assert.That(root.Children[0].Name).IsEqualTo("[0]");
        await Assert.That(root.Children[2].Name).IsEqualTo("[2]");
        await Assert.That(root.Children[2].ValuePreview).IsEqualTo("\"z\"");
    }

    [Test]
    public async Task NestedStructureRecurses()
    {
        var root = JsonTree.Parse("""{"user": {"name": "Ada", "roles": ["admin", "dev"]}}""");

        var user = root!.Children[0];
        await Assert.That(user.Name).IsEqualTo("user");
        await Assert.That(user.Kind).IsEqualTo(JsonNodeKind.Object);

        var roles = user.Children[1];
        await Assert.That(roles.Name).IsEqualTo("roles");
        await Assert.That(roles.Kind).IsEqualTo(JsonNodeKind.Array);
        await Assert.That(roles.Children.Count).IsEqualTo(2);
        await Assert.That(roles.Children[0].ValuePreview).IsEqualTo("\"admin\"");
    }

    [Test]
    public async Task SingularSummariesReadNaturally()
    {
        await Assert.That(JsonTree.Parse("""{"only": 1}""")!.ValuePreview).IsEqualTo("{ 1 field }");
        await Assert.That(JsonTree.Parse("[1]")!.ValuePreview).IsEqualTo("[ 1 item ]");
    }

    [Test]
    public async Task LongStringLeafIsTruncated()
    {
        var big = new string('x', 500);
        var root = JsonTree.Parse($"\"{big}\"");

        await Assert.That(root!.ValuePreview.Length).IsLessThan(big.Length);
        await Assert.That(root.ValuePreview.EndsWith('…')).IsTrue();
    }

    [Test]
    public async Task A_short_multi_line_string_still_previews_on_one_line()
    {
        // Only strings past the length cap used to lose their newlines, so a
        // short two-line value made its row two lines tall.
        var root = JsonTree.Parse("""{"note": "line 1\nline 2"}""");

        await Assert.That(root!.Children[0].ValuePreview).IsEqualTo("\"line 1\\nline 2\"");
    }

    [Test]
    public async Task A_string_previews_its_characters_unescaped()
    {
        var root = JsonTree.Parse("""["Ann <ann@example.com>"]""");

        await Assert.That(root!.Children[0].ValuePreview).IsEqualTo("\"Ann <ann@example.com>\"");
    }

    [Test]
    public async Task Every_node_knows_its_path_from_the_root()
    {
        var root = JsonTree.Parse("""{"user": {"roles": ["admin", "dev"]}, "user2": 1}""")!;

        var dev = root.Children[0].Children[0].Children[1];

        await Assert.That(root.Path).IsEmpty();
        await Assert.That(dev.Path).IsEquivalentTo(
            [JsonPathSegment.Member("user", 0), JsonPathSegment.Member("roles", 0), JsonPathSegment.Element(1)]);
        await Assert.That(dev.IsElement).IsTrue();
        await Assert.That(root.Children[1].Path.Single()).IsEqualTo(JsonPathSegment.Member("user2", 1));
    }

    [Test]
    public async Task A_small_document_opens_whole()
    {
        var root = JsonTree.Parse("""{"cc": ["a@x.io", "b@x.io"], "meta": {"n": 1}}""")!;

        await Assert.That(root.ExpandedByDefault).IsTrue();
        await Assert.That(root.Children[0].ExpandedByDefault).IsTrue();
        await Assert.That(root.Children[1].ExpandedByDefault).IsTrue();
    }

    [Test]
    public async Task A_large_document_opens_its_outline()
    {
        // Three arrays of 40: the root and the first two fit in the rows the
        // tree opens with, the third would take it past them.
        var list = "[" + string.Join(",", Enumerable.Range(0, 40)) + "]";
        var root = JsonTree.Parse($$"""{"a": {{list}}, "b": {{list}}, "c": {{list}}}""")!;

        await Assert.That(root.ExpandedByDefault).IsTrue();
        await Assert.That(root.Children[0].ExpandedByDefault).IsTrue();
        await Assert.That(root.Children[1].ExpandedByDefault).IsTrue();
        await Assert.That(root.Children[2].ExpandedByDefault).IsFalse();
    }

    [Test]
    public async Task A_level_opens_before_the_one_under_it()
    {
        // "a" holds a 95-item array. Opened depth first it would take the rows
        // "b" needs, and "b" is higher in the outline, so "b" goes first.
        var list = "[" + string.Join(",", Enumerable.Range(0, 95)) + "]";
        var root = JsonTree.Parse($$"""{"a": {"deep": {{list}}}, "b": [1, 2, 3]}""")!;

        await Assert.That(root.Children[0].ExpandedByDefault).IsTrue();
        await Assert.That(root.Children[1].ExpandedByDefault).IsTrue();
        await Assert.That(root.Children[0].Children[0].ExpandedByDefault).IsFalse();
    }

    [Test]
    public async Task Reads_nesting_past_json_documents_default_depth()
    {
        var json = new string('[', 100) + new string(']', 100);

        await Assert.That(JsonTree.Parse(json)).IsNotNull();
    }

    [Test]
    public async Task ValueAt_gives_what_a_copy_wants()
    {
        const string json = """{"to": ["Ann <ann@example.com>"], "n": 1.50, "ok": false, "o": {"k": [1]}}""";
        var root = JsonTree.Parse(json)!;

        await Assert.That(JsonTree.ValueAt(json, root.Children[0].Children[0].Path)).IsEqualTo("Ann <ann@example.com>");
        await Assert.That(JsonTree.ValueAt(json, root.Children[1].Path)).IsEqualTo("1.50");
        await Assert.That(JsonTree.ValueAt(json, root.Children[2].Path)).IsEqualTo("false");
        await Assert.That(JsonTree.ValueAt(json, root.Children[3].Path)).IsEqualTo("{\n  \"k\": [\n    1\n  ]\n}");
        await Assert.That(JsonTree.ValueAt(json, [])).IsEqualTo(JsonText.TryFormat(json, true, out var all) ? all : null);
    }

    [Test]
    public async Task ValueAt_follows_a_repeated_key_by_position()
    {
        const string json = """{"a": 1, "a": 2}""";
        var root = JsonTree.Parse(json)!;

        await Assert.That(JsonTree.ValueAt(json, root.Children[1].Path)).IsEqualTo("2");
    }

    [Test]
    public async Task ValueAt_is_null_for_a_path_that_leads_nowhere()
    {
        await Assert.That(JsonTree.ValueAt("[1]", [JsonPathSegment.Element(3)])).IsNull();
        await Assert.That(JsonTree.ValueAt("[1]", [JsonPathSegment.Member("a", 0)])).IsNull();
        await Assert.That(JsonTree.ValueAt("not json", [])).IsNull();
    }
}
