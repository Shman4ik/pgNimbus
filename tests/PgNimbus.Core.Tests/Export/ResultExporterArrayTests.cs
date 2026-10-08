using System.Text.Json;
using PgNimbus.Core.Export;

namespace PgNimbus.Core.Tests.Export;

/// <summary>
/// The two array shapes Npgsql hands over that a flat walk gets wrong: a
/// multi-dimensional array (a CLR <c>T[,]</c>, which enumerates flat) and a
/// <c>bytea[]</c> (a <c>byte[][]</c>, whose elements are themselves arrays).
/// The literal writer is <c>PgValueSyntax.FormatArray</c>; these hold the
/// export paths that go through it, and JSON, which walks arrays itself.
/// </summary>
public sealed class ResultExporterArrayTests
{
    private static JsonElement JsonCell(object value)
    {
        using var stream = new MemoryStream();
        ResultExporter.WriteJson(stream, ["v"], [[value]]);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement[0].GetProperty("v").Clone();
    }

    // The cell's JSON without the export's indentation.
    private static string Compact(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            element.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Test]
    public async Task Json_keeps_each_dimension_of_a_multidimensional_array()
    {
        // A foreach over an int[,] runs flat and wrote [1,2,3,4].
        await Assert.That(Compact(JsonCell(new[,] { { 1, 2 }, { 3, 4 } }))).IsEqualTo("[[1,2],[3,4]]");
        await Assert.That(Compact(JsonCell(new[,,] { { { 1 }, { 2 } }, { { 3 }, { 4 } } }))).IsEqualTo("[[[1],[2]],[[3],[4]]]");
        await Assert.That(Compact(JsonCell(new string?[,] { { "a", null } }))).IsEqualTo("""[["a",null]]""");
        await Assert.That(Compact(JsonCell(new int[0, 2]))).IsEqualTo("[]");
    }

    // Value-type arrays arrive with nullable elements (int?[], int?[,]):
    // ConnectionProfile.ArrayNullability. A NULL element is null in JSON and
    // NULL in the literal an INSERT copy writes.
    [Test]
    public async Task Null_elements_of_a_value_type_array_survive_json_and_insert()
    {
        await Assert.That(Compact(JsonCell(new int?[] { 1, null, 3 }))).IsEqualTo("[1,null,3]");
        await Assert.That(Compact(JsonCell(new int?[,] { { 1, null }, { 3, 4 } }))).IsEqualTo("[[1,null],[3,4]]");
        await Assert.That(ResultExporter.FormatSqlLiteral(new int?[] { 1, null, 3 }, "integer[]")).IsEqualTo("'{1,NULL,3}'");
        await Assert.That(ResultExporter.FormatSqlLiteral(new int?[,] { { 1, null }, { 3, 4 } }, "integer[]")).IsEqualTo("'{{1,NULL},{3,4}}'");
    }

    [Test]
    public async Task Json_writes_a_bytea_array_as_one_base64_string_per_element()
    {
        var cell = JsonCell(new[] { new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, new byte[] { 0x01 } });

        await Assert.That(cell.GetArrayLength()).IsEqualTo(2);
        await Assert.That(cell[0].GetString()).IsEqualTo(Convert.ToBase64String([0xDE, 0xAD, 0xBE, 0xEF]));
        await Assert.That(cell[1].GetString()).IsEqualTo("AQ==");
    }

    [Test]
    public async Task An_insert_copy_writes_a_bytea_array_as_hex_elements()
    {
        // The exporter's element formatter had a bytea arm, but the nested-array
        // case ran first, so the copy wrote '{{222,173,190,239}}'. The literal
        // holds backslashes, so it goes out as an escape string.
        var literal = ResultExporter.FormatSqlLiteral(new[] { new byte[] { 0xDE, 0xAD, 0xBE, 0xEF } }, "bytea[]");

        await Assert.That(literal).IsEqualTo("""E'{"\\\\xDEADBEEF"}'""");
    }

    [Test]
    public async Task An_insert_copy_writes_a_multidimensional_bytea_array_by_dimension()
    {
        var value = new byte[,][] { { new byte[] { 0xDE, 0xAD } }, { new byte[] { 0xBE, 0xEF } } };
        using var writer = new StringWriter();
        ResultExporter.WriteInsert(writer, "blobs", ["parts"], [[value]], ["bytea[]"]);

        await Assert.That(writer.ToString())
            .IsEqualTo("""INSERT INTO blobs (parts) VALUES (E'{{"\\\\xDEAD"},{"\\\\xBEEF"}}');""" + "\n");
    }
}
