using System.Text.Json;
using PgNimbus.Core.Json;
using PgNimbus.Core.Tests.Text;

namespace PgNimbus.Core.Tests.Json;

public class JsonTextTests
{
    private static string Pretty(string json) => JsonText.TryFormat(json, indented: true, out var result)
        ? result
        : throw new InvalidOperationException($"not JSON: {json}");

    private static string Minified(string json) => JsonText.TryFormat(json, indented: false, out var result)
        ? result
        : throw new InvalidOperationException($"not JSON: {json}");

    [Test]
    public async Task Indents_objects_and_arrays_two_spaces_with_newline_breaks()
    {
        await Assert.That(Pretty("""{"a":1,"b":[true,null],"c":{}, "d": []}""")).IsEqualTo(
            "{\n  \"a\": 1,\n  \"b\": [\n    true,\n    null\n  ],\n  \"c\": {},\n  \"d\": []\n}");
    }

    [Test]
    public async Task Minifies_onto_one_line_without_spaces()
    {
        await Assert.That(Minified("{\n  \"a\": 1,\n  \"b\": [ 2, 3 ]\n}")).IsEqualTo("""{"a":1,"b":[2,3]}""");
    }

    /// <summary>
    /// The reported case: a list of addresses in "Name &lt;address&gt;" form read
    /// as <c>\u003C</c>…<c>\u003E</c>, because the writer's encoder escaped every
    /// HTML-sensitive character. JSON needs none of them escaped.
    /// </summary>
    [Test]
    [Arguments("Ann <ann@example.com>")]
    [Arguments("it's a+b & c")]
    [Arguments("ok 😀")]
    [Arguments("Привет, мир")]
    [Arguments("`tick` = \u00e9")]
    public async Task Writes_characters_json_can_hold_as_themselves(string value)
    {
        var json = JsonSerializer.Serialize(value, JsonTestContext.Default.String);

        await Assert.That(Minified(json)).IsEqualTo("\"" + value + "\"");
    }

    [Test]
    public async Task Escapes_only_the_quote_the_backslash_and_control_characters()
    {
        await Assert.That(Minified("""["say \"hi\"", "C:\\temp", "a\nb\tc", "\u0001", "\/"]"""))
            .IsEqualTo("""["say \"hi\"","C:\\temp","a\nb\tc","\u0001","/"]""");
    }

    [Test]
    public async Task Keeps_a_number_exactly_as_written()
    {
        await Assert.That(Minified("[1.10, 123456789012345678901234567890, -0.0, 1e400, 2E-3]"))
            .IsEqualTo("[1.10,123456789012345678901234567890,-0.0,1e400,2E-3]");
    }

    [Test]
    public async Task Escapes_keys_the_same_way()
    {
        await Assert.That(Minified("""{"a\"b": 1, "<k>": 2}""")).IsEqualTo("""{"a\"b":1,"<k>":2}""");
    }

    [Test]
    public async Task Keeps_duplicate_keys_in_their_order()
    {
        // A json (not jsonb) value keeps them, and a viewer must not merge them.
        await Assert.That(Minified("""{"a": 1, "a": 2}""")).IsEqualTo("""{"a":1,"a":2}""");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("{\"a\": 1,}")]
    [Arguments("{\"a\": 1} trailing")]
    [Arguments("[1, 2")]
    [Arguments("// comment\n1")]
    [Arguments("\"\\uD800\"")]
    public async Task Refuses_what_is_not_one_json_value(string text)
    {
        await Assert.That(JsonText.TryFormat(text, indented: true, out var result)).IsFalse();
        await Assert.That(result).IsEqualTo(text);
    }

    [Test]
    public async Task A_scalar_is_formatted_too()
    {
        await Assert.That(Pretty(" 42 ")).IsEqualTo("42");
        await Assert.That(Pretty("\"x\"")).IsEqualTo("\"x\"");
    }

    [Test]
    public async Task Reads_nesting_deeper_than_json_documents_default()
    {
        const int depth = 200;
        var json = new string('[', depth) + new string(']', depth);

        await Assert.That(JsonText.TryFormat(json, indented: false, out var result)).IsTrue();
        await Assert.That(result).IsEqualTo(json);
    }

    [Test]
    public async Task Refuses_nesting_past_its_limit_instead_of_recursing()
    {
        var json = new string('[', 100_000) + new string(']', 100_000);
        var formatted = true;
        HostileText.RunBounded(() => formatted = JsonText.TryFormat(json, indented: true, out _), TimeSpan.FromSeconds(30));

        await Assert.That(formatted).IsFalse();
    }

    [Test]
    public async Task Round_trips_what_it_formats()
    {
        const string json = """{"to":["Ann <ann@example.com>","bob@example.com"],"n":[1.5,-2,3e2],"ok":true,"none":null,"nested":{"x":{"y":[[]]}}}""";

        var pretty = Pretty(json);

        await Assert.That(Minified(pretty)).IsEqualTo(json);
    }

    [Test]
    public async Task Quote_escapes_like_the_formatter()
    {
        await Assert.That(JsonText.Quote("a\"b\\c\nd <e>")).IsEqualTo("\"a\\\"b\\\\c\\nd <e>\"");
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class JsonTestContext : System.Text.Json.Serialization.JsonSerializerContext;
