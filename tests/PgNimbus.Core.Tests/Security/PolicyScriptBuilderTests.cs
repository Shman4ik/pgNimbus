using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// The re-create script for an RLS policy: the statement itself, the inert
/// comment, and the two things the audit found wrong with it — a role named
/// PUBLIC written as the keyword (finding 12) and a table name breaking out of
/// the comment (finding 11).
/// </summary>
public class PolicyScriptBuilderTests
{
    private static RlsPolicyInfo Policy(string schema, string table, IReadOnlyList<string?> roles, string? withCheck = null) =>
        new(schema, table, "orders_own", Permissive: true, roles, "SELECT", "customer = current_user", withCheck);

    private static string N(string text) => text.ReplaceLineEndings("\n");

    [Test]
    public async Task A_policy_is_rendered_as_the_create_statement()
    {
        var sql = PolicyScriptBuilder.Create(Policy("sales", "orders", ["app_ro", "App Reader"], "true"), rowSecurityEnabled: true);

        await Assert.That(N(sql)).IsEqualTo(N("""
            CREATE POLICY orders_own ON sales.orders
                AS PERMISSIVE
                FOR SELECT
                TO app_ro, "App Reader"
                USING (customer = current_user)
                WITH CHECK (true);
            """));
    }

    [Test]
    public async Task The_real_public_is_the_keyword()
    {
        // polroles {0} comes back as a null entry; no roles at all means the same.
        await Assert.That(PolicyScriptBuilder.Create(Policy("sales", "orders", [null]), true)).Contains("    TO PUBLIC\n");
        await Assert.That(PolicyScriptBuilder.Create(Policy("sales", "orders", []), true)).Contains("    TO PUBLIC\n");
    }

    [Test]
    public async Task A_role_named_public_is_quoted()
    {
        var sql = PolicyScriptBuilder.Create(Policy("sales", "orders", ["PUBLIC"]), true);

        await Assert.That(sql).Contains("    TO \"PUBLIC\"\n");
        await Assert.That(sql).DoesNotContain("TO PUBLIC\n");
    }

    [Test]
    public async Task An_inert_policy_gets_the_comment()
    {
        var sql = PolicyScriptBuilder.Create(Policy("sales", "orders", ["app_ro"]), rowSecurityEnabled: false);

        await Assert.That(N(sql)).StartsWith(N("""
            -- sales.orders does not have row-level security enabled, so this
            -- policy is not applied to anyone. It takes effect only after:
            --   ALTER TABLE sales.orders ENABLE ROW LEVEL SECURITY;
            CREATE POLICY orders_own ON sales.orders
            """));
    }

    [Test]
    [Arguments("x\nALTER ROLE eve SUPERUSER;--")]
    [Arguments("x\r\nALTER ROLE eve SUPERUSER;--")]
    public async Task The_comment_cannot_be_broken_out_of_by_a_newline_in_the_table_name(string table)
    {
        var sql = PolicyScriptBuilder.Create(Policy("sales", table, ["app_ro"]), rowSecurityEnabled: false);

        // Split the way the server's lexer ends a comment: at \n or \r.
        var lines = sql.Split(['\n', '\r']);
        var commentLines = lines.TakeWhile(l => l.StartsWith("--", StringComparison.Ordinal)).ToList();

        await Assert.That(commentLines.Count).IsEqualTo(3);

        // The lexer-backed splitter is the judge of what would run: an escaped
        // comment would add a statement, so the count is the proof.
        var statements = PgNimbus.Core.Query.SqlScriptSplitter.Split(sql);
        await Assert.That(statements.Count).IsEqualTo(1);
        await Assert.That(ScriptText.FirstStatementLine(statements[0])).StartsWith("CREATE POLICY ");

        // The statement still names the table exactly, quoted.
        await Assert.That(sql).Contains($"CREATE POLICY orders_own ON sales.\"{table}\"");
    }
}
