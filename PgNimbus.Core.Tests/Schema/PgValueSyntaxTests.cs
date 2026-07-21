using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class PgValueSyntaxTests
{
    [Test]
    [Arguments("{}")]
    [Arguments("{1,2,3}")]
    [Arguments(" {1,2,3} ")]
    [Arguments("{{1,2},{3,4}}")]
    [Arguments("""{"a b","c,d"}""")]
    [Arguments("""{"say \"hi\""}""")]
    [Arguments("""{"do""ble"}""")]
    [Arguments("""{"(1,x)","(2,y)"}""")]
    [Arguments("[1:3]={1,2,3}")]
    public async Task AcceptsWellFormedArrays(string text)
    {
        await Assert.That(PgValueSyntax.ValidateArray(text)).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("1,2,3")]
    [Arguments("{1,2,3")]
    [Arguments("{1,2}}")]
    [Arguments("{1,2} extra")]
    [Arguments("""{"unterminated}""")]
    [Arguments("[1:3]{1,2,3}")]
    public async Task RejectsMalformedArrays(string text)
    {
        await Assert.That(PgValueSyntax.ValidateArray(text)).IsNotNull();
    }

    [Test]
    [Arguments("()")]
    [Arguments("(1,abc)")]
    [Arguments("(1,,3)")]
    [Arguments("""(1,"a,b")""")]
    [Arguments("""("{1,2}",x)""")]
    [Arguments("((1,2),3)")]
    public async Task AcceptsWellFormedComposites(string text)
    {
        await Assert.That(PgValueSyntax.ValidateComposite(text)).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("1,abc")]
    [Arguments("(1,abc")]
    [Arguments("(1))")]
    [Arguments("(1) tail")]
    [Arguments("""("open)""")]
    public async Task RejectsMalformedComposites(string text)
    {
        await Assert.That(PgValueSyntax.ValidateComposite(text)).IsNotNull();
    }

    [Test]
    public async Task FormatsClrArraysAsPostgresLiterals()
    {
        await Assert.That(PgValueSyntax.FormatArray(new[] { 1, 2, 3 })).IsEqualTo("{1,2,3}");
        await Assert.That(PgValueSyntax.FormatArray(new[] { "new", "sale" })).IsEqualTo("{new,sale}");
        await Assert.That(PgValueSyntax.FormatArray(new[] { true, false })).IsEqualTo("{t,f}");
        await Assert.That(PgValueSyntax.FormatArray(new[] { new[] { 1 }, new[] { 2 } })).IsEqualTo("{{1},{2}}");
        await Assert.That(PgValueSyntax.FormatArray(new string?[] { "a", null })).IsEqualTo("{a,NULL}");
    }

    [Test]
    public async Task QuotesArrayElementsTheWayPostgresWould()
    {
        await Assert.That(PgValueSyntax.FormatArray(new[] { "a b", "c,d" })).IsEqualTo("""{"a b","c,d"}""");
        await Assert.That(PgValueSyntax.FormatArray(new[] { "" })).IsEqualTo("""{""}""");
        await Assert.That(PgValueSyntax.FormatArray(new[] { "null" })).IsEqualTo("""{"null"}""");
        await Assert.That(PgValueSyntax.FormatArray(new[] { "say \"hi\"" })).IsEqualTo("""{"say \"hi\""}""");
        await Assert.That(PgValueSyntax.FormatArray(new[] { @"back\slash" })).IsEqualTo("""{"back\\slash"}""");
    }

    [Test]
    public async Task FormattedArraysPassTheirOwnValidation()
    {
        var formatted = PgValueSyntax.FormatArray(new[] { "plain", "with space", "with,comma", "with\"quote" });

        await Assert.That(PgValueSyntax.ValidateArray(formatted)).IsNull();
    }

    [Test]
    public async Task BracesInsideCompositesAndParensInsideArraysAreOrdinaryCharacters()
    {
        // Only the literal's own delimiter pair is structural — Postgres
        // treats the other pair as plain data when unquoted.
        await Assert.That(PgValueSyntax.ValidateComposite("({1,x)")).IsNull();
        await Assert.That(PgValueSyntax.ValidateArray("{(1,x}")).IsNull();
    }

    [Test]
    [Arguments("integer", "42")]
    [Arguments("integer", "-42")]
    [Arguments("integer", "  7  ")]
    [Arguments("smallint", "32767")]
    [Arguments("smallint", "-32768")]
    [Arguments("bigint", "9223372036854775807")]
    [Arguments("numeric(10,2)", "12.34")]
    [Arguments("numeric", "-0.5")]
    [Arguments("numeric", "1e6")]
    [Arguments("numeric", "NaN")]
    [Arguments("double precision", "3.14159")]
    [Arguments("double precision", "-Infinity")]
    [Arguments("real", "1.5")]
    [Arguments("uuid", "00000000-0000-0000-0000-000000000000")]
    [Arguments("uuid", "0b7cd5c8-6b1e-4f2a-9c3d-1e2f3a4b5c6d")]
    // Types with no client-side check defer to Postgres.
    [Arguments("text", "anything at all")]
    [Arguments("jsonb", "{not really validated}")]
    [Arguments("inet", "10.0.0.1")]
    // A domain over integer is validated against its resolved base type.
    [Arguments("integer", "")]
    public async Task AcceptsWellFormedScalars(string dataType, string value)
    {
        await Assert.That(PgValueSyntax.ValidateScalar(dataType, value)).IsNull();
    }

    [Test]
    [Arguments("integer", "abc")]
    [Arguments("integer", "1.0")]
    [Arguments("integer", "1,000")]
    [Arguments("integer", "2147483648")]      // one past int max
    [Arguments("integer", "-2147483649")]     // one past int min
    [Arguments("smallint", "40000")]
    [Arguments("bigint", "9223372036854775808")]
    [Arguments("numeric(10,2)", "1.2.3")]
    [Arguments("double precision", "not-a-number")]
    [Arguments("real", "12x")]
    [Arguments("uuid", "not-a-uuid")]
    [Arguments("uuid", "12345")]
    public async Task RejectsMalformedScalars(string dataType, string value)
    {
        await Assert.That(PgValueSyntax.ValidateScalar(dataType, value)).IsNotNull();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("""{"a": 1, "b": [true, null, "x"]}""")]
    [Arguments("[1, 2, 3]")]
    [Arguments("  { \"nested\": { \"deep\": 42 } }  ")] // leading/trailing space is fine
    // json/jsonb both accept a bare scalar, so ValidateJson must too.
    [Arguments("42")]
    [Arguments("-3.14")]
    [Arguments("\"just a string\"")]
    [Arguments("true")]
    [Arguments("null")]
    // Blank defers to the server / column default.
    [Arguments("")]
    [Arguments("   ")]
    public async Task AcceptsWellFormedJson(string value)
    {
        await Assert.That(PgValueSyntax.ValidateJson(value)).IsNull();
    }

    [Test]
    [Arguments("{not really validated}")]
    [Arguments("""{"a": 1,}""")]          // trailing comma — Postgres rejects it too
    [Arguments("""{"a": 1""")]            // unclosed brace
    [Arguments("[1, 2,]")]                 // trailing comma in array
    [Arguments("'single quotes'")]         // JSON requires double quotes
    [Arguments("undefined")]
    [Arguments("{ bare: 1 }")]             // unquoted key
    public async Task RejectsMalformedJson(string value)
    {
        await Assert.That(PgValueSyntax.ValidateJson(value)).IsNotNull();
    }
}
