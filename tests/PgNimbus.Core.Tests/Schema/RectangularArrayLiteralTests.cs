using PgNimbus.Core.Export;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class RectangularArrayLiteralTests
{
    [Test]
    public async Task PreservesTwoDimensionalShape()
    {
        var value = new[,] { { 1, 2, 3 }, { 4, 5, 6 } };
        await Assert.That(PgValueSyntax.FormatArray(value)).IsEqualTo("{{1,2,3},{4,5,6}}");
    }

    [Test]
    public async Task PreservesThreeDimensionalShape()
    {
        var value = new[,,] { { { 1, 2 }, { 3, 4 } }, { { 5, 6 }, { 7, 8 } } };
        await Assert.That(PgValueSyntax.FormatArray(value))
            .IsEqualTo("{{{1,2},{3,4}},{{5,6},{7,8}}}");
    }

    [Test]
    public async Task KeepsNullsAndEscapesRectangularArrayElements()
    {
        var value = new string?[,] { { null, "NULL", "" }, { "a,b", "say \"hi\"", @"back\slash" } };
        await Assert.That(PgValueSyntax.FormatArray(value))
            .IsEqualTo("""{{NULL,"NULL",""},{"a,b","say \"hi\"","back\\slash"}}""");
    }

    [Test]
    public async Task AppliesElementFormatterAtLeaves()
    {
        var value = new[,] { { 1, 2 }, { 3, 4 } };
        await Assert.That(PgValueSyntax.FormatArray(value, item => $"value {item}"))
            .IsEqualTo("""{{"value 1","value 2"},{"value 3","value 4"}}""");
    }

    [Test]
    public async Task PreservesOneDimensionalAndJaggedArrays()
    {
        await Assert.That(PgValueSyntax.FormatArray(new[] { 1, 2 })).IsEqualTo("{1,2}");
        await Assert.That(PgValueSyntax.FormatArray(new[] { new[] { 1, 2 }, new[] { 3, 4 } }))
            .IsEqualTo("{{1,2},{3,4}}");
        await Assert.That(PgValueSyntax.FormatArray(new int[0, 2])).IsEqualTo("{}");
    }

    [Test]
    public async Task SqlExportPreservesArrayShape()
    {
        await Assert.That(ResultExporter.FormatSqlLiteral(new[,] { { 1, 2 }, { 3, 4 } }, "integer[]"))
            .IsEqualTo("'{{1,2},{3,4}}'");
        await Assert.That(ResultExporter.FormatSqlLiteral(new[,,] { { { 1, 2 } }, { { 3, 4 } } }, "integer[]"))
            .IsEqualTo("'{{{1,2}},{{3,4}}}'");
    }
    [Test]
    public async Task InsertExportPreservesShapeNullsAndSqlEscaping()
    {
        var value = new string?[,] { { "O'Reilly", null }, { "a,b", @"back\slash" } };
        using var writer = new StringWriter();
        ResultExporter.WriteInsert(writer, "matrices", ["value"], [new object?[] { value }], ["text[]"]);
        await Assert.That(writer.ToString())
            .IsEqualTo("""INSERT INTO matrices (value) VALUES (E'{{O''Reilly,NULL},{"a,b","back\\\\slash"}}');""" + "\n");
    }

}
