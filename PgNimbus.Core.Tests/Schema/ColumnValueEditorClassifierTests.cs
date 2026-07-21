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
    [Arguments('b', 'N', "money")]    // round-trips through its CLR decimal — no cast needed
    [Arguments('b', 'U', "uuid")]     // round-trips through Guid — no cast needed
    [Arguments('b', 'U', "hstore")]   // display needs an extension mapping — stays text, not CastText
    [Arguments('b', 'D', "time")]     // no seconds-capable picker — stays text
    [Arguments('b', 'T', "interval")]
    public async Task EverythingElseStaysText(char typtype, char typcategory, string typname)
    {
        await Assert.That(ColumnValueEditorClassifier.Classify(typtype, typcategory, typname)).IsEqualTo(ColumnValueEditor.Text);
    }

    [Test]
    // Types Postgres won't implicitly assign from text (no text→type assignment
    // cast) round-trip an inline edit through CAST(text AS type). Whole
    // categories qualify — network ('I'), geometric ('G'), range/multirange
    // ('R'), bit-string ('V') — including user-defined ranges; a handful of
    // category-'U' types are named individually.
    [Arguments('b', 'I', "inet")]
    [Arguments('b', 'I', "cidr")]
    [Arguments('b', 'G', "point")]
    [Arguments('b', 'G', "box")]
    [Arguments('b', 'G', "polygon")]
    [Arguments('r', 'R', "int4range")]
    [Arguments('m', 'R', "int4multirange")]
    [Arguments('r', 'R', "myrange")]   // a user-defined range, caught by category not name
    [Arguments('b', 'V', "bit")]
    [Arguments('b', 'V', "varbit")]
    [Arguments('b', 'U', "xml")]
    [Arguments('b', 'U', "tsvector")]
    [Arguments('b', 'U', "tsquery")]
    [Arguments('b', 'U', "jsonpath")]  // not JSON-shaped, but still needs a server-side cast
    [Arguments('b', 'U', "macaddr")]
    [Arguments('b', 'U', "macaddr8")]
    [Arguments('b', 'U', "pg_lsn")]
    [Arguments('b', 'U', "bytea")]
    public async Task NoAssignmentCastTypesUseCastText(char typtype, char typcategory, string typname)
    {
        await Assert.That(ColumnValueEditorClassifier.Classify(typtype, typcategory, typname)).IsEqualTo(ColumnValueEditor.CastText);
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
