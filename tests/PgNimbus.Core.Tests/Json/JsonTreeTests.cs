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
}
