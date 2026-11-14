using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Tests;

/// <summary>
/// The error marker against a real server: each case runs through
/// <see cref="QueryViewModel"/>'s own run path and the real <see cref="QueryEngine"/>,
/// and checks that the marked text is the token the server complained about. This is
/// what proves Npgsql sends the text unchanged (a rewrite would shift the position).
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like the other server-backed tests.
/// </summary>
[NotInParallel]
public class ErrorMarkerLiveTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to fail a statement on.");
        }
    }

    private static NpgsqlDataSource Open() => NpgsqlDataSource.Create(ConnectionProfile.ForAppSession(ConnectionString!));

    private static QueryViewModel NewTab(NpgsqlDataSource dataSource, string sql) =>
        new(new QueryEngine(dataSource), new ExplainService(dataSource)) { Sql = sql };

    // Runs the whole tab and returns what the marker covers, or null.
    private static async Task<string?> RunTabAsync(string sql)
    {
        await using var dataSource = Open();
        string? marked = null;
        await Ui.Run(async () =>
        {
            var tab = NewTab(dataSource, sql);
            await tab.RunCommand.ExecuteAsync(null);
            await Assert.That(tab.HasError).IsTrue();
            marked = Marked(tab);
        });
        return marked;
    }

    private static string? Marked(QueryViewModel tab) =>
        tab.ErrorMarker is { } span ? tab.Sql.Substring(span.Start, span.Length) : null;

    [Test]
    public async Task A_syntax_error_mid_statement()
    {
        SkipIfNoConnection();
        await Assert.That(await RunTabAsync("SELECT 1 FROM WHERE relname = 'x'")).IsEqualTo("WHERE");
    }

    [Test]
    public async Task Leading_blank_lines_and_a_trailing_semicolon()
    {
        SkipIfNoConnection();
        await Assert.That(await RunTabAsync("\n\n    SELECT 1 FROM WHERE relname = 'x';\n  ")).IsEqualTo("WHERE");
    }

    [Test]
    public async Task An_undefined_column()
    {
        SkipIfNoConnection();
        await Assert.That(await RunTabAsync("SELECT relname,\n       nmae\n  FROM pg_class")).IsEqualTo("nmae");
    }

    [Test]
    public async Task An_emoji_and_a_non_bmp_character_before_the_error()
    {
        SkipIfNoConnection();
        // U+1F600 and U+20000 (CJK Extension B): one character each to the server.
        await Assert.That(await RunTabAsync("SELECT '\U0001F600 \U00020000' AS e, nope FROM pg_class")).IsEqualTo("nope");
    }

    [Test]
    public async Task The_error_in_statement_two_of_a_script()
    {
        SkipIfNoConnection();
        await Assert.That(await RunTabAsync("SELECT 1;\n  SELECT nope2 FROM pg_class;\nSELECT 3;")).IsEqualTo("nope2");
    }

    [Test]
    public async Task The_error_in_statement_three_of_a_script()
    {
        SkipIfNoConnection();
        await Assert.That(await RunTabAsync("SELECT 1;\nSELECT '\U0001F600';\n\n   SELECT 1 FROM WHERE x;")).IsEqualTo("WHERE");
    }

    [Test]
    public async Task A_syntax_error_at_end_of_input()
    {
        SkipIfNoConnection();
        // The server points one past the end; the last token is what is marked.
        await Assert.That(await RunTabAsync("SELECT (1 +   \n")).IsEqualTo("+");
    }

    [Test]
    public async Task A_selection_that_starts_mid_document()
    {
        SkipIfNoConnection();
        await using var dataSource = Open();
        await Ui.Run(async () =>
        {
            const string document = "SELECT 1;\n-- pick the next one\n  SELECT '\U0001F600', nope3 FROM pg_class;\nSELECT 4;";
            var tab = NewTab(dataSource, document);
            var start = document.IndexOf("  SELECT '", StringComparison.Ordinal);
            var end = document.IndexOf("\nSELECT 4", StringComparison.Ordinal);
            // What the editor pushes on a selection change.
            tab.SelectedSql = document[start..end];
            tab.SelectedSqlOffset = start;

            await tab.RunCommand.ExecuteAsync(null);

            await Assert.That(Marked(tab)).IsEqualTo("nope3");
            await Assert.That(tab.Status).EndsWith("(line 3, column 15)");
        });
    }

    [Test]
    public async Task The_statement_at_the_caret()
    {
        SkipIfNoConnection();
        await using var dataSource = Open();
        await Ui.Run(async () =>
        {
            const string document = "SELECT 1;\n\n  SELECT nope4 FROM pg_class  ;\nSELECT 3;";
            var tab = NewTab(dataSource, document);
            var span = SqlScriptSplitter.StatementSpanAt(document, document.IndexOf("nope4", StringComparison.Ordinal))!.Value;

            await tab.RunStatementAsync(document[span.Start..span.End], span.Start);

            await Assert.That(Marked(tab)).IsEqualTo("nope4");
        });
    }

    [Test]
    public async Task An_error_inside_a_plpgsql_body_is_not_marked()
    {
        SkipIfNoConnection();
        await using var dataSource = Open();
        await using (var create = dataSource.CreateCommand(
            """
            CREATE OR REPLACE FUNCTION public.pgnimbus_errpos_fn() RETURNS int AS $$
            BEGIN
                PERFORM no_such_column FROM pg_class;
                RETURN 1;
            END;
            $$ LANGUAGE plpgsql;
            """))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await Ui.Run(async () =>
            {
                var tab = NewTab(dataSource, "SELECT 1 + public.pgnimbus_errpos_fn()");
                await tab.RunCommand.ExecuteAsync(null);

                await Assert.That(tab.HasError).IsTrue();
                await Assert.That(tab.Status).Contains("no_such_column");
                await Assert.That(tab.ErrorMarker).IsNull();
                await Assert.That(tab.Status).DoesNotContain("(line");
            });
        }
        finally
        {
            await using var drop = dataSource.CreateCommand("DROP FUNCTION IF EXISTS public.pgnimbus_errpos_fn()");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Test]
    public async Task The_engine_reports_the_position_of_the_text_it_was_given()
    {
        SkipIfNoConnection();
        await using var dataSource = Open();
        const string sql = "SELECT '\U0001F600' AS e, nope5 FROM pg_class";
        var result = await new QueryEngine(dataSource).ExecuteAsync(sql, CancellationToken.None);

        await Assert.That(result).IsTypeOf<QueryError>();
        var span = SqlErrorLocation.Map(sql, ((QueryError)result).Position);
        await Assert.That(span is { } s ? sql.Substring(s.Start, s.Length) : null).IsEqualTo("nope5");
    }
}
