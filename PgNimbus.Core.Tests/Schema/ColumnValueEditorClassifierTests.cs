using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

public class ColumnValueEditorClassifierTests
{
    [Test]
    [Arguments('e', 'E', "mood", ColumnValueEditor.Enum)]
    [Arguments('c', 'C', "address", ColumnValueEditor.Composite)]
    [Arguments('b', 'A', "_int4", ColumnValueEditor.Array)]
    [Arguments('b', 'B', "bool", ColumnValueEditor.Boolean)]
    [Arguments('b', 'D', "date", ColumnValueEditor.Date)]
    [Arguments('b', 'D', "timestamp", ColumnValueEditor.Timestamp)]
    [Arguments('b', 'D', "timestamptz", ColumnValueEditor.Timestamp)]
    [Arguments('b', 'U', "json", ColumnValueEditor.Json)]
    [Arguments('b', 'U', "jsonb", ColumnValueEditor.Json)]
    public async Task ClassifiesDedicatedEditors(char typtype, char typcategory, string typname, ColumnValueEditor expected)
    {
        await Assert.That(ColumnValueEditorClassifier.Classify(typtype, typcategory, typname)).IsEqualTo(expected);
    }

    [Test]
    [Arguments('b', 'S', "text")]
    [Arguments('b', 'N', "int4")]
    [Arguments('b', 'N', "numeric")]
    [Arguments('b', 'U', "uuid")]
    [Arguments('b', 'U', "jsonpath")] // a jsonpath expression isn't JSON-shaped — stays text
    [Arguments('b', 'U', "hstore")]   // hstore's "k"=>"v" literal isn't JSON — stays text
    [Arguments('b', 'D', "time")]     // no seconds-capable picker — stays text
    [Arguments('b', 'T', "interval")]
    [Arguments('r', 'R', "int4range")]
    public async Task EverythingElseStaysText(char typtype, char typcategory, string typname)
    {
        await Assert.That(ColumnValueEditorClassifier.Classify(typtype, typcategory, typname)).IsEqualTo(ColumnValueEditor.Text);
    }

    [Test]
    public async Task EnumWinsOverCategory()
    {
        // An enum's typcategory is 'E', but typtype alone must decide — a
        // hypothetical mismatch should still land on the enum editor.
        await Assert.That(ColumnValueEditorClassifier.Classify('e', 'X', "whatever")).IsEqualTo(ColumnValueEditor.Enum);
    }

    [Test]
    public async Task ArrayOfEnumsIsAnArrayNotAnEnum()
    {
        // "_mood" (mood[]) is typtype 'b', typcategory 'A' — the array editor
        // (text + validation) applies, not the element type's dropdown.
        await Assert.That(ColumnValueEditorClassifier.Classify('b', 'A', "_mood")).IsEqualTo(ColumnValueEditor.Array);
    }
}
