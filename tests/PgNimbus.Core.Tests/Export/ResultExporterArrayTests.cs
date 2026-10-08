using System.Text;
using PgNimbus.Core.Export;

namespace PgNimbus.Core.Tests.Export;

/// <summary>
/// Arrays as the app's sessions read them: value-type elements nullable
/// (<c>int?[]</c>, <see cref="Connections.ConnectionProfile.ArrayNullability"/>),
/// and a multi-dimensional array as a rectangular CLR one (<c>int?[2,2]</c>),
/// whose foreach is flat. The INSERT copy and the JSON export must keep both
/// the NULLs and the dimensions.
/// </summary>
public sealed class ResultExporterArrayTests
{
    private static string Insert(object? value, string type)
    {
        var writer = new StringWriter();
        ResultExporter.WriteInsert(writer, "t", ["v"], [[value]], columnTypes: [type]);
        return writer.ToString();
    }

    private static string Json(object? value, string type)
    {
        using var stream = new MemoryStream();
        ResultExporter.WriteJson(stream, ["v"], [[value]], columnTypes: [type]);
        return string.Concat(Encoding.UTF8.GetString(stream.ToArray()).Where(c => !char.IsWhiteSpace(c)));
    }

    [Test]
    public async Task An_insert_writes_null_elements_and_every_dimension()
    {
        await Assert.That(Insert(new int?[] { 1, null, 3 }, "integer[]")).Contains("'{1,NULL,3}'");
        await Assert.That(Insert(new int?[,] { { 1, null }, { 3, 4 } }, "integer[]")).Contains("'{{1,NULL},{3,4}}'");
    }

    [Test]
    public async Task Json_writes_a_multi_dimensional_array_as_nested_arrays()
    {
        await Assert.That(Json(new int?[] { 1, null, 3 }, "integer[]")).IsEqualTo("""[{"v":[1,null,3]}]""");
        // Postgres's own to_json writes [[1,null],[3,4]]; this used to be [1,null,3,4].
        await Assert.That(Json(new int?[,] { { 1, null }, { 3, 4 } }, "integer[]")).IsEqualTo("""[{"v":[[1,null],[3,4]]}]""");
        await Assert.That(Json(new[,] { { "a", null }, { "c", "d" } }, "text[]")).IsEqualTo("""[{"v":[["a",null],["c","d"]]}]""");
    }
}
