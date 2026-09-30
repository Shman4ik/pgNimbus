using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// The <c>ALTER DEFAULT PRIVILEGES</c> a default-privileges row is copied out
/// as: the statement, its comment, PUBLIC versus a role named PUBLIC
/// (finding 12), and a name breaking out of the comment (finding 11).
/// </summary>
public class DefaultPrivilegeScriptBuilderTests
{
    private static string N(string text) => text.ReplaceLineEndings("\n");

    [Test]
    public async Task A_row_is_rendered_as_the_statement_under_its_comment()
    {
        var sql = DefaultPrivilegeScriptBuilder.Build(
            "app_owner", "sales", SecurableKind.Table, "app_ro", withGrantOption: true,
            [PrivilegeKind.Select, PrivilegeKind.Insert]);

        await Assert.That(N(sql)).IsEqualTo(N("""
            -- Applies to tables created from now on by app_owner in schema sales.
            -- Objects that already exist are untouched: those need
            -- GRANT … ON ALL TABLES IN SCHEMA …, which this does not replace.
            --
            -- The key is the CREATING role, not the schema. Pointed at the wrong creator
            -- this statement runs fine and does nothing.
            ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA sales
                GRANT SELECT, INSERT ON TABLES TO app_ro WITH GRANT OPTION;
            """));
    }

    [Test]
    public async Task The_database_wide_default_has_no_schema_clause()
    {
        var sql = DefaultPrivilegeScriptBuilder.Build(
            "app_owner", null, SecurableKind.Function, null, withGrantOption: false, [PrivilegeKind.Execute]);

        await Assert.That(sql).Contains("by app_owner in any schema.");
        await Assert.That(sql).Contains("ALTER DEFAULT PRIVILEGES FOR ROLE app_owner\n    GRANT EXECUTE ON FUNCTIONS TO PUBLIC;");
    }

    [Test]
    public async Task A_role_named_public_is_quoted_and_the_real_public_is_not()
    {
        var named = DefaultPrivilegeScriptBuilder.Build("o", "s", SecurableKind.Table, "PUBLIC", false, [PrivilegeKind.Select]);
        var everyone = DefaultPrivilegeScriptBuilder.Build("o", "s", SecurableKind.Table, null, false, [PrivilegeKind.Select]);

        await Assert.That(named).EndsWith("ON TABLES TO \"PUBLIC\";");
        await Assert.That(everyone).EndsWith("ON TABLES TO PUBLIC;");
    }

    [Test]
    [Arguments("x\nALTER ROLE eve SUPERUSER;--", "sales")]
    [Arguments("x\r\nALTER ROLE eve SUPERUSER;--", "sales")]
    [Arguments("app_owner", "x\nALTER ROLE eve SUPERUSER;--")]
    [Arguments("app_owner", "x\r\nALTER ROLE eve SUPERUSER;--")]
    public async Task The_comment_cannot_be_broken_out_of_by_a_newline_in_a_name(string owner, string schema)
    {
        var sql = DefaultPrivilegeScriptBuilder.Build(owner, schema, SecurableKind.Table, "app_ro", false, [PrivilegeKind.Select]);

        // Split the way the server's lexer ends a comment: at \n or \r.
        var lines = sql.Split(['\n', '\r']);
        var commentLines = lines.TakeWhile(l => l.StartsWith("--", StringComparison.Ordinal)).ToList();

        await Assert.That(commentLines.Count).IsEqualTo(6);

        // The lexer-backed splitter is the judge of what would run: an escaped
        // comment would add a statement, so the count is the proof.
        var statements = PgNimbus.Core.Query.SqlScriptSplitter.Split(sql);
        await Assert.That(statements.Count).IsEqualTo(1);
        await Assert.That(ScriptText.FirstStatementLine(statements[0])).StartsWith("ALTER DEFAULT PRIVILEGES ");

        // The statement keeps both names exactly, quoted.
        var quotedOwner = PgNimbus.Core.Query.SqlIdentifier.QuoteIfNeeded(owner);
        var quotedSchema = PgNimbus.Core.Query.SqlIdentifier.QuoteIfNeeded(schema);
        await Assert.That(sql).Contains($"FOR ROLE {quotedOwner} IN SCHEMA {quotedSchema}");
        await Assert.That(quotedOwner + quotedSchema).Contains("\"x");
    }
}
