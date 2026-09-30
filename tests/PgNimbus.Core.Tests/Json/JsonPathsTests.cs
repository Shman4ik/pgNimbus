using PgNimbus.Core.Json;

namespace PgNimbus.Core.Tests.Json;

public class JsonPathsTests
{
    private static IReadOnlyList<JsonPathSegment> PathTo(string json, params string[] names)
    {
        var node = JsonTree.Parse(json)!;
        foreach (var name in names)
        {
            node = node.Children.Single(c => c.Name == name);
        }

        return node.Path;
    }

    private const string Order = """{"items": [{"sku": "NIM-1"}], "it's": {"a b": 1}, "Owner": "x"}""";

    [Test]
    public async Task A_scalar_reads_as_text_through_arrows()
    {
        await Assert.That(JsonPaths.ToSql("metadata", castToJsonb: false, PathTo(Order, "items", "[0]", "sku"), asText: true))
            .IsEqualTo("metadata->'items'->0->>'sku'");
    }

    [Test]
    public async Task A_container_stays_json()
    {
        await Assert.That(JsonPaths.ToSql("metadata", castToJsonb: false, PathTo(Order, "items"), asText: false))
            .IsEqualTo("metadata->'items'");
    }

    [Test]
    public async Task Keys_are_sql_literals_and_the_column_is_quoted_when_it_must_be()
    {
        await Assert.That(JsonPaths.ToSql("Meta Data", castToJsonb: false, PathTo(Order, "it's", "a b"), asText: true))
            .IsEqualTo("\"Meta Data\"->'it''s'->>'a b'");
    }

    [Test]
    public async Task A_key_with_a_backslash_is_an_escape_string()
    {
        var path = JsonTree.Parse("""{"a\\b": 1}""")!.Children[0].Path;

        await Assert.That(JsonPaths.ToSql("doc", castToJsonb: false, path, asText: true)).IsEqualTo("doc->>E'a\\\\b'");
    }

    [Test]
    public async Task A_text_column_is_cast_before_the_operators()
    {
        await Assert.That(JsonPaths.ToSql("payload", castToJsonb: true, PathTo(Order, "Owner"), asText: true))
            .IsEqualTo("payload::jsonb->>'Owner'");
    }

    [Test]
    public async Task The_root_is_the_column()
    {
        await Assert.That(JsonPaths.ToSql("metadata", castToJsonb: false, [], asText: false)).IsEqualTo("metadata");
        await Assert.That(JsonPaths.ToJsonPath([])).IsEqualTo("$");
    }

    [Test]
    public async Task Json_path_quotes_any_key_that_is_not_a_plain_identifier()
    {
        await Assert.That(JsonPaths.ToJsonPath(PathTo(Order, "items", "[0]", "sku"))).IsEqualTo("$.items[0].sku");
        await Assert.That(JsonPaths.ToJsonPath(PathTo(Order, "it's", "a b"))).IsEqualTo("$.\"it's\".\"a b\"");
        await Assert.That(JsonPaths.ToJsonPath(JsonTree.Parse("""{"a\"b": {"1x": 0}}""")!.Children[0].Children[0].Path))
            .IsEqualTo("$.\"a\\\"b\".\"1x\"");
    }
}
