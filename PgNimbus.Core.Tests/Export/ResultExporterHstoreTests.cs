using System.Text;
using PgNimbus.Core.Export;

namespace PgNimbus.Core.Tests.Export;

/// <summary>
/// hstore comes back from Npgsql as a <c>Dictionary&lt;string,string&gt;</c>;
/// every export/copy path must render its value, never the CLR type name.
/// </summary>
public sealed class ResultExporterHstoreTests
{
    private static Dictionary<string, string?> Sample() => new()
    {
        ["lang"] = "de",
        ["referrer"] = "organic",
    };

    [Test]
    public async Task TsvRendersHstoreAsPostgresLiteral()
    {
        var writer = new StringWriter();
        ResultExporter.WriteTsv(writer, ["attrs"], [[Sample()]]);

        // TSV doesn't quote/escape the way CSV does, so the hstore literal appears verbatim.
        await Assert.That(writer.ToString()).Contains("\"lang\"=>\"de\", \"referrer\"=>\"organic\"");
        await Assert.That(writer.ToString()).DoesNotContain("Dictionary");
    }

    [Test]
    public async Task JsonRendersHstoreAsObject()
    {
        using var stream = new MemoryStream();
        ResultExporter.WriteJson(stream, ["attrs"], [[Sample()]]);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        await Assert.That(json).Contains("\"lang\": \"de\"");
        await Assert.That(json).Contains("\"referrer\": \"organic\"");
        await Assert.That(json).DoesNotContain("Dictionary");
    }

    [Test]
    public async Task JsonRendersHstoreNullValueAsJsonNull()
    {
        using var stream = new MemoryStream();
        ResultExporter.WriteJson(stream, ["attrs"], [[new Dictionary<string, string?> { ["missing"] = null }]]);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        await Assert.That(json).Contains("\"missing\": null");
    }
}
