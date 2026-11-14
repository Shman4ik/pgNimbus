using PgNimbus.Core.Query;

namespace PgNimbus.Core.Tests.Query;

public class SqlErrorLocationTests
{
    // What the editor would underline, as text, for a mapped span.
    private static string? Marked(string document, SqlErrorSpan? span) =>
        span is { } s ? document.Substring(s.Start, s.Length) : null;

    [Test]
    public async Task Plain_ascii_marks_the_token_at_the_position()
    {
        const string sql = "SELECT * FROM missing_table";
        // Postgres: 'relation "missing_table" does not exist', position 15.
        var span = SqlErrorLocation.Map(sql, 15);

        await Assert.That(span).IsEqualTo(new SqlErrorSpan(14, 13));
        await Assert.That(Marked(sql, span)).IsEqualTo("missing_table");
    }

    [Test]
    public async Task A_position_inside_a_token_marks_the_whole_token()
    {
        const string sql = "SELECT colmun FROM t";
        var span = SqlErrorLocation.Map(sql, 10);

        await Assert.That(Marked(sql, span)).IsEqualTo("colmun");
    }

    [Test]
    public async Task Multi_line_statement_maps_and_reports_line_and_column()
    {
        const string sql = "SELECT id,\n       nmae\nFROM users";
        var position = sql.IndexOf("nmae", StringComparison.Ordinal) + 1;
        var span = SqlErrorLocation.Map(sql, position);

        await Assert.That(Marked(sql, span)).IsEqualTo("nmae");
        await Assert.That(SqlErrorLocation.LineColumn(sql, span!.Value.Start)).IsEqualTo((2, 8));
    }

    [Test]
    public async Task Crlf_counts_as_one_line_break()
    {
        const string sql = "SELECT 1,\r\n  2,\r\n  bad_col FROM t";
        // The server counts \r and \n as two characters each line, like .NET does.
        var position = sql.IndexOf("bad_col", StringComparison.Ordinal) + 1;
        var span = SqlErrorLocation.Map(sql, position);

        await Assert.That(Marked(sql, span)).IsEqualTo("bad_col");
        await Assert.That(SqlErrorLocation.LineColumn(sql, span!.Value.Start)).IsEqualTo((3, 3));
    }

    [Test]
    public async Task Surrogate_pairs_count_as_one_character()
    {
        // The emoji is one character to Postgres and two UTF-16 units to .NET.
        const string sql = "SELECT '\U0001F600\U0001F600' AS e, nope FROM t";
        var codePoints = sql.EnumerateRunes().Count();
        var utf16Index = sql.IndexOf("nope", StringComparison.Ordinal);
        var serverPosition = sql[..utf16Index].EnumerateRunes().Count() + 1;

        await Assert.That(serverPosition).IsEqualTo(utf16Index - 1);
        await Assert.That(codePoints).IsEqualTo(sql.Length - 2);

        var span = SqlErrorLocation.Map(sql, serverPosition);
        await Assert.That(Marked(sql, span)).IsEqualTo("nope");
        // The column counts the emoji once too.
        await Assert.That(SqlErrorLocation.LineColumn(sql, span!.Value.Start)).IsEqualTo((1, serverPosition));
    }

    [Test]
    public async Task Statement_n_of_a_script_is_shifted_by_its_start()
    {
        const string script = "SELECT 1;\n\n  SELECT 2;\nSELECT x FROM nowhere;";
        // The engine sends the third statement trimmed: "SELECT x FROM nowhere".
        var span = SqlErrorLocation.MapInScript(script, 0, 2, 15);

        await Assert.That(Marked(script, span)).IsEqualTo("nowhere");
        await Assert.That(span!.Value.Start).IsEqualTo(script.IndexOf("nowhere", StringComparison.Ordinal));
    }

    [Test]
    public async Task Leading_whitespace_trimmed_before_sending_is_accounted_for()
    {
        const string script = "\n\n   SELECT bogus";
        var span = SqlErrorLocation.MapInScript(script, 0, 0, 8);

        await Assert.That(Marked(script, span)).IsEqualTo("bogus");
    }

    [Test]
    public async Task A_selection_run_is_shifted_by_the_selection_start()
    {
        const string document = "SELECT 1;\nSELECT 2;\n  SELECT oops FROM t;\nSELECT 4;";
        var selectionStart = document.IndexOf("  SELECT oops", StringComparison.Ordinal);
        var selection = document.Substring(selectionStart, "  SELECT oops FROM t;".Length);

        var span = SqlErrorLocation.MapInScript(selection, selectionStart, 0, 8);

        await Assert.That(Marked(document, span)).IsEqualTo("oops");
    }

    [Test]
    public async Task A_selection_holding_a_script_maps_its_second_statement()
    {
        const string document = "-- header\nSELECT 1; SELECT broken(;\n";
        var selectionStart = document.IndexOf("SELECT 1", StringComparison.Ordinal);
        var selection = document[selectionStart..];

        // "SELECT broken(" fails at end of input: position 15 is one past its end.
        var span = SqlErrorLocation.MapInScript(selection, selectionStart, 1, 15);

        await Assert.That(Marked(document, span)).IsEqualTo("(");
    }

    [Test]
    public async Task One_past_the_end_marks_the_last_token()
    {
        const string sql = "SELECT (1 + ";
        var trimmed = sql.TrimEnd();
        var span = SqlErrorLocation.Map(trimmed, trimmed.Length + 1);

        await Assert.That(Marked(trimmed, span)).IsEqualTo("+");
    }

    [Test]
    public async Task A_position_further_past_the_end_marks_nothing()
    {
        await Assert.That(SqlErrorLocation.Map("SELECT 1", 10)).IsNull();
        // Eight characters, nine UTF-16 units: 9 is "at end of input", 10 is past it.
        await Assert.That(SqlErrorLocation.Map("SELECT \U0001F600", 9)).IsEqualTo(new SqlErrorSpan(7, 2));
        await Assert.That(SqlErrorLocation.Map("SELECT \U0001F600", 10)).IsNull();
        await Assert.That(SqlErrorLocation.MapInScript("SELECT 1; SELECT 2", 0, 2, 1)).IsNull();
    }

    [Test]
    public async Task No_position_marks_nothing()
    {
        await Assert.That(SqlErrorLocation.Map("SELECT 1", null)).IsNull();
        await Assert.That(SqlErrorLocation.Map("SELECT 1", 0)).IsNull();
        await Assert.That(SqlErrorLocation.Map("", 1)).IsNull();
        await Assert.That(SqlErrorLocation.MapInScript("SELECT 1", 0, 0, null)).IsNull();
    }

    [Test]
    public async Task A_position_in_whitespace_marks_one_character()
    {
        const string sql = "SELECT 1   FROM t";
        var span = SqlErrorLocation.Map(sql, 10);

        await Assert.That(span).IsEqualTo(new SqlErrorSpan(9, 1));
    }

    [Test]
    public async Task Split_spans_match_split()
    {
        const string script = "  select 1 ;\n-- c\n;select $$;$$  ;\r\n select 3\n";
        var spans = SqlScriptSplitter.SplitSpans(script);
        var texts = spans.Select(s => script[s.Start..s.End]).ToList();

        await Assert.That(string.Join('|', texts)).IsEqualTo(string.Join('|', SqlScriptSplitter.Split(script)));
    }
}
