using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class PgTypeCategorizerTests
{
    [Test]
    // Numeric family — every width/precision and the float/money/oid spellings.
    [Arguments("smallint", PgTypeCategory.Numeric)]
    [Arguments("integer", PgTypeCategory.Numeric)]
    [Arguments("bigint", PgTypeCategory.Numeric)]
    [Arguments("int4", PgTypeCategory.Numeric)]
    [Arguments("numeric(10,2)", PgTypeCategory.Numeric)]
    [Arguments("numeric", PgTypeCategory.Numeric)]
    [Arguments("double precision", PgTypeCategory.Numeric)]
    [Arguments("real", PgTypeCategory.Numeric)]
    [Arguments("money", PgTypeCategory.Numeric)]
    // Text family.
    [Arguments("text", PgTypeCategory.Text)]
    [Arguments("character varying(255)", PgTypeCategory.Text)]
    [Arguments("character varying", PgTypeCategory.Text)]
    [Arguments("character(10)", PgTypeCategory.Text)]
    [Arguments("citext", PgTypeCategory.Text)]
    [Arguments("name", PgTypeCategory.Text)]
    // Boolean.
    [Arguments("boolean", PgTypeCategory.Boolean)]
    [Arguments("bool", PgTypeCategory.Boolean)]
    // Date/time family.
    [Arguments("date", PgTypeCategory.DateTime)]
    [Arguments("timestamp without time zone", PgTypeCategory.DateTime)]
    [Arguments("timestamp with time zone", PgTypeCategory.DateTime)]
    [Arguments("timestamptz", PgTypeCategory.DateTime)]
    [Arguments("time with time zone", PgTypeCategory.DateTime)]
    [Arguments("interval", PgTypeCategory.DateTime)]
    // Singletons.
    [Arguments("uuid", PgTypeCategory.Uuid)]
    [Arguments("json", PgTypeCategory.Json)]
    [Arguments("jsonb", PgTypeCategory.Json)]
    [Arguments("hstore", PgTypeCategory.Json)]
    [Arguments("inet", PgTypeCategory.Network)]
    [Arguments("cidr", PgTypeCategory.Network)]
    [Arguments("macaddr", PgTypeCategory.Network)]
    [Arguments("point", PgTypeCategory.Geometric)]
    [Arguments("polygon", PgTypeCategory.Geometric)]
    [Arguments("box", PgTypeCategory.Geometric)]
    [Arguments("bytea", PgTypeCategory.Binary)]
    [Arguments("bit(8)", PgTypeCategory.BitString)]
    [Arguments("bit varying", PgTypeCategory.BitString)]
    [Arguments("varbit", PgTypeCategory.BitString)]
    [Arguments("vector(3)", PgTypeCategory.Vector)]
    [Arguments("halfvec", PgTypeCategory.Vector)]
    [Arguments("tsvector", PgTypeCategory.FullText)]
    [Arguments("tsquery", PgTypeCategory.FullText)]
    // Ranges + multiranges.
    [Arguments("int4range", PgTypeCategory.Range)]
    [Arguments("numrange", PgTypeCategory.Range)]
    [Arguments("tstzrange", PgTypeCategory.Range)]
    [Arguments("int4multirange", PgTypeCategory.Range)]
    // Unknown / user types fall through.
    [Arguments("mood", PgTypeCategory.Other)]
    [Arguments("ltree", PgTypeCategory.Other)]
    [Arguments("xml", PgTypeCategory.Text)]
    public async Task Categorizes(string typeName, PgTypeCategory expected)
    {
        await Assert.That(PgTypeCategorizer.Categorize(typeName)).IsEqualTo(expected);
    }

    [Test]
    // Arrays win over the element family, from either spelling format_type/wire
    // uses ("integer[]") or the internal catalog spelling ("_int4").
    [Arguments("integer[]")]
    [Arguments("text[]")]
    [Arguments("uuid[]")]
    [Arguments("numeric(10,2)[]")]
    [Arguments("_int4")]
    [Arguments("mood[]")]
    public async Task ArraysAreArrays(string typeName)
    {
        await Assert.That(PgTypeCategorizer.Categorize(typeName)).IsEqualTo(PgTypeCategory.Array);
    }

    [Test]
    [Arguments("INTEGER", PgTypeCategory.Numeric)]
    [Arguments("  jsonb  ", PgTypeCategory.Json)]
    [Arguments("public.mood", PgTypeCategory.Other)]
    public async Task NormalizesCaseWhitespaceAndSchema(string typeName, PgTypeCategory expected)
    {
        await Assert.That(PgTypeCategorizer.Categorize(typeName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task BlankIsOther(string? typeName)
    {
        await Assert.That(PgTypeCategorizer.Categorize(typeName)).IsEqualTo(PgTypeCategory.Other);
    }

    [Test]
    // The editor identifies enum/composite, which a bare name can't.
    [Arguments("device_status", ColumnValueEditor.Enum, PgTypeCategory.Enum)]
    [Arguments("address", ColumnValueEditor.Composite, PgTypeCategory.Composite)]
    // Other editors don't override the name-based family.
    [Arguments("integer", ColumnValueEditor.Text, PgTypeCategory.Numeric)]
    [Arguments("boolean", ColumnValueEditor.Boolean, PgTypeCategory.Boolean)]
    [Arguments("jsonb", ColumnValueEditor.Text, PgTypeCategory.Json)]
    public async Task CategorizeColumnUsesEditorForEnumAndComposite(string declared, ColumnValueEditor editor, PgTypeCategory expected)
    {
        await Assert.That(PgTypeCategorizer.CategorizeColumn(declared, null, editor)).IsEqualTo(expected);
    }

    [Test]
    // A domain (declared name classifies as Other) falls back to its base type.
    [Arguments("email_addr", "citext", PgTypeCategory.Text)]
    [Arguments("commerce.email_addr", "citext", PgTypeCategory.Text)]
    [Arguments("us_postal", "text", PgTypeCategory.Text)]
    [Arguments("positive_int", "integer", PgTypeCategory.Numeric)]
    // A declared type that already classifies keeps its own family, base ignored.
    [Arguments("integer", "text", PgTypeCategory.Numeric)]
    [Arguments("jsonb", null, PgTypeCategory.Json)]
    // No base type to fall back to → stays Other.
    [Arguments("mood", null, PgTypeCategory.Other)]
    public async Task DomainColumnsClassifyByBaseType(string declared, string? baseType, PgTypeCategory expected)
    {
        await Assert.That(PgTypeCategorizer.Categorize(PgTypeCategorizer.ClassifierType(declared, baseType)))
            .IsEqualTo(expected);
    }
}
