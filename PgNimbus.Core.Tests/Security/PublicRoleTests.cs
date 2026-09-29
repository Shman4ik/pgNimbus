using Npgsql;
using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// Only the lowercase <c>public</c> is reserved, so <c>CREATE ROLE "PUBLIC"</c>
/// is legal, and a permissions UI that spells PUBLIC as a string cannot tell
/// the two apart: granting to the role granted to everyone, revoking from it
/// left its access in place (security audit 2026-09, finding 12). PUBLIC is
/// <c>null</c> end to end now; this creates the role for real and checks that
/// the catalog reads, the scripts and the server all keep them apart.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c>; the role is cluster-wide, hence
/// <c>NotInParallel</c>, and it is dropped in <c>finally</c>.
/// </summary>
[NotInParallel]
public class PublicRoleTests
{
    private const string Schema = "pgnimbus_public_scratch";
    private const string NamedPublic = "\"PUBLIC\"";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to create a role named PUBLIC on.");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task A_role_named_public_and_the_keyword_are_told_apart_end_to_end()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var ct = CancellationToken.None;

        await ExecuteAsync(dataSource, $"""
            DROP SCHEMA IF EXISTS {Schema} CASCADE;
            DROP ROLE IF EXISTS {NamedPublic};
            CREATE ROLE {NamedPublic} NOLOGIN;
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.t (id int PRIMARY KEY);
            GRANT SELECT ON {Schema}.t TO {NamedPublic};
            GRANT INSERT ON {Schema}.t TO PUBLIC;
            ALTER TABLE {Schema}.t ENABLE ROW LEVEL SECURITY;
            CREATE POLICY for_named ON {Schema}.t FOR SELECT TO {NamedPublic} USING (true);
            CREATE POLICY for_everyone ON {Schema}.t FOR INSERT TO PUBLIC WITH CHECK (true);
            """);
        try
        {
            var service = new PrivilegeService(dataSource);
            var table = (await service.GetSecurablesAsync(SecurableKind.Table, Schema, ct)).Single(s => s.Name == "t");

            // The ACL: the role's grant carries its name, PUBLIC's carries null.
            // (The first GRANT also materializes the owner's own entries, which
            // are not what this is about.)
            var acl = await service.GetAclAsync(table, ct);
            var granted = acl.Entries.Where(e => e.Grantee != acl.Owner).ToList();
            var select = granted.Single(e => e.Privilege == PrivilegeKind.Select);
            var insert = granted.Single(e => e.Privilege == PrivilegeKind.Insert);
            await Assert.That(select.Grantee).IsEqualTo("PUBLIC");
            await Assert.That(select.IsPublic).IsFalse();
            await Assert.That(select.GranteeLabel).IsEqualTo("\"PUBLIC\"");
            await Assert.That(insert.Grantee).IsNull();
            await Assert.That(insert.IsPublic).IsTrue();

            // The policies: the role by name, PUBLIC as a null entry.
            var state = (await service.GetRlsAsync(Schema, ct)).Single(t => t.Table == "t");
            var named = state.Policies.Single(p => p.Name == "for_named");
            var everyone = state.Policies.Single(p => p.Name == "for_everyone");
            await Assert.That(named.Roles).IsEquivalentTo(new string?[] { "PUBLIC" });
            await Assert.That(named.AppliesToEveryone).IsFalse();
            await Assert.That(everyone.Roles).IsEquivalentTo(new string?[] { null });
            await Assert.That(everyone.AppliesToEveryone).IsTrue();

            await Assert.That(PolicyScriptBuilder.Create(named, true)).Contains("    TO \"PUBLIC\"\n");
            await Assert.That(PolicyScriptBuilder.Create(everyone, true)).Contains("    TO PUBLIC\n");

            // The revoke script for the role, run for real, takes the role's grant
            // away and leaves PUBLIC's alone — the case the audit reproduced.
            var revoke = GrantScriptBuilder.Build(
                [new PrivilegeChange(table, select.Grantee, PrivilegeKind.Select, Grant: false)]);
            await Assert.That(revoke).IsEqualTo($"REVOKE SELECT ON TABLE {Schema}.t FROM \"PUBLIC\";");
            await ExecuteAsync(dataSource, revoke);

            var after = await service.GetAclAsync(table, ct);
            var left = after.Entries.Where(e => e.Grantee != after.Owner).ToList();
            await Assert.That(left.Any(e => e.Privilege == PrivilegeKind.Select)).IsFalse();
            await Assert.That(left.Single(e => e.Privilege == PrivilegeKind.Insert).Grantee).IsNull();
        }
        finally
        {
            await ExecuteAsync(dataSource, $"""
                DROP SCHEMA IF EXISTS {Schema} CASCADE;
                DROP ROLE IF EXISTS {NamedPublic};
                """);
        }
    }
}
