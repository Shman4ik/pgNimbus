using Npgsql;
using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// Runs every catalog query in <see cref="RoleService"/> and
/// <see cref="PrivilegeService"/> against a real server.
///
/// The pure halves — <c>RoleGraph</c>, <c>EffectivePrivilegeResolver</c> — are
/// covered by their own tests with no server in sight. What those cannot catch
/// is a query that does not parse, a column that moved between major versions,
/// or an <c>aclexplode</c> shape that is not what the reader expects. Those fail
/// at runtime in front of a user, so they are checked here instead.
///
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like <c>QueryEngineCompositeTests</c>:
/// unset (a plain local run) every test skips; CI's <c>postgres:17</c> service
/// container sets it and they run for real. The connected role has to be able to
/// create roles — on a managed server without that, these skip rather than fail.
/// </summary>
// Every test shares one scratch fixture of roles and objects, and roles are
// cluster-wide, so these must not overlap with each other.
[NotInParallel]
public class SecurityCatalogTests
{
    private const string Prefix = "pgnimbus_sec_";
    private const string Readers = Prefix + "readers";
    private const string Writers = Prefix + "writers";
    private const string AppRo = Prefix + "app_ro";
    private const string Legacy = Prefix + "legacy";
    private const string Schema = Prefix + "schema";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres available to test the security catalog reads against.");
        }
    }

    private static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString!);

    /// <summary>
    /// A small but deliberately awkward fixture: an inheritance chain
    /// (app_ro → readers), a NOINHERIT membership (app_ro → legacy), a column
    /// grant, a default privilege, an RLS table and a policy on a table where
    /// RLS was never enabled. Torn down and rebuilt per test so a crashed run
    /// cannot leave a stale shape behind.
    /// </summary>
    private static async Task ResetFixtureAsync(NpgsqlDataSource dataSource)
    {
        await ExecuteAsync(dataSource, $"""
            DROP SCHEMA IF EXISTS {Schema} CASCADE;
            DROP ROLE IF EXISTS {AppRo};
            DROP ROLE IF EXISTS {Writers};
            DROP ROLE IF EXISTS {Readers};
            DROP ROLE IF EXISTS {Legacy};

            CREATE ROLE {Readers} NOLOGIN;
            CREATE ROLE {Writers} NOLOGIN;
            CREATE ROLE {Legacy} NOLOGIN;
            CREATE ROLE {AppRo} NOLOGIN CONNECTION LIMIT 5 VALID UNTIL '2030-01-01';
            COMMENT ON ROLE {Readers} IS 'read-only group';
            ALTER ROLE {AppRo} SET statement_timeout = '30s';

            GRANT {Readers} TO {Writers};
            GRANT {Writers} TO {AppRo};

            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.orders (id bigint PRIMARY KEY, customer text);
            CREATE TABLE {Schema}.secret (id bigint PRIMARY KEY, note text);
            CREATE TABLE {Schema}.inert (id int);
            CREATE SEQUENCE {Schema}.order_seq;

            GRANT USAGE ON SCHEMA {Schema} TO {Readers};
            GRANT SELECT ON {Schema}.orders TO {Readers};
            GRANT SELECT (id) ON {Schema}.secret TO {Readers};
            ALTER DEFAULT PRIVILEGES IN SCHEMA {Schema} GRANT SELECT ON TABLES TO {Readers};

            ALTER TABLE {Schema}.orders ENABLE ROW LEVEL SECURITY;
            CREATE POLICY orders_own ON {Schema}.orders FOR SELECT TO {Readers} USING (customer = current_user);
            CREATE POLICY inert_policy ON {Schema}.inert USING (true);
            """);

        // NOINHERIT is set on the membership on PG16+ and on the member role
        // before that, so the two servers reach the same state by different
        // statements. RoleService reads whichever column exists; this makes sure
        // there is something non-default for it to read either way.
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var sql = connection.PostgreSqlVersion.Major >= 16
            ? $"GRANT {Legacy} TO {AppRo} WITH INHERIT FALSE"
            : $"GRANT {Legacy} TO {AppRo}";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task DropFixtureAsync(NpgsqlDataSource dataSource) =>
        await ExecuteAsync(dataSource, $"""
            DROP SCHEMA IF EXISTS {Schema} CASCADE;
            DROP ROLE IF EXISTS {AppRo};
            DROP ROLE IF EXISTS {Writers};
            DROP ROLE IF EXISTS {Readers};
            DROP ROLE IF EXISTS {Legacy};
            """);

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        var ct = CancellationToken.None;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Test]
    public async Task Roles_carry_their_attributes_settings_and_comment()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var ct = CancellationToken.None;
        await ResetFixtureAsync(dataSource);

        try
        {
            var roles = await new RoleService(dataSource).GetRolesAsync(includePredefined: false, ct);

            var appRo = roles.Single(r => r.Name == AppRo);
            await Assert.That(appRo.ConnectionLimit).IsEqualTo(5);
            await Assert.That(appRo.ValidUntil).IsNotNull();
            await Assert.That(appRo.Settings).Contains("statement_timeout=30s");

            var readers = roles.Single(r => r.Name == Readers);
            await Assert.That(readers.Comment).IsEqualTo("read-only group");
            await Assert.That(readers.CanLogin).IsFalse();

            // includePredefined: false must drop the built-ins initdb created.
            await Assert.That(roles.Any(r => r.IsPredefined)).IsFalse();
        }
        finally
        {
            await DropFixtureAsync(dataSource);
        }
    }

    /// <summary>
    /// The membership read and the graph over it, together — the version-guarded
    /// half of <see cref="RoleService"/> and the NOINHERIT rule that decides
    /// whether a privilege is reported at all.
    /// </summary>
    [Test]
    public async Task Membership_reflects_inheritance_and_the_noinherit_edge()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var ct = CancellationToken.None;
        await ResetFixtureAsync(dataSource);

        try
        {
            var service = new RoleService(dataSource);
            var graph = RoleGraph.Build(
                await service.GetRolesAsync(includePredefined: true, ct),
                await service.GetMembershipsAsync(ct));

            // app_ro -> writers -> readers, nearest first.
            var inherited = graph.InheritedGroups(AppRo);
            await Assert.That(string.Join("|", inherited)).IsEqualTo($"{Writers}|{Readers}");

            // The NOINHERIT membership is real, so it shows in the tree...
            var memberOf = graph.MemberOf(AppRo);
            await Assert.That(memberOf.Any(n => n.Role == Legacy)).IsTrue();
            await Assert.That(memberOf.Single(n => n.Role == Legacy).Inherits).IsFalse();

            // ...but its privileges are dormant until SET ROLE, so it is not inherited.
            await Assert.That(inherited).DoesNotContain(Legacy);
        }
        finally
        {
            await DropFixtureAsync(dataSource);
        }
    }

    [Test]
    public async Task Table_acl_expands_and_a_null_acl_is_reported_as_default()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var ct = CancellationToken.None;
        await ResetFixtureAsync(dataSource);

        try
        {
            var service = new PrivilegeService(dataSource);
            var tables = await service.GetSecurablesAsync(SecurableKind.Table, Schema, ct);

            var orders = tables.Single(t => t.Name == "orders");
            var acl = await service.GetAclAsync(orders, ct);
            await Assert.That(acl.IsDefaultAcl).IsFalse();
            await Assert.That(acl.Entries.Any(e => e.Grantee == Readers && e.Privilege == PrivilegeKind.Select)).IsTrue();

            // "inert" was never granted to anyone, so its relacl is NULL — which
            // means "owner has everything, defaults apply", not "no privileges".
            // Rendering that as an empty grid is the bug this flag exists to stop.
            var inert = tables.Single(t => t.Name == "inert");
            var inertAcl = await service.GetAclAsync(inert, ct);
            await Assert.That(inertAcl.IsDefaultAcl).IsTrue();
            await Assert.That(inertAcl.Entries).IsEmpty();
        }
        finally
        {
            await DropFixtureAsync(dataSource);
        }
    }

    [Test]
    public async Task Column_grants_default_privileges_and_policies_are_read()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var ct = CancellationToken.None;
        await ResetFixtureAsync(dataSource);

        try
        {
            var service = new PrivilegeService(dataSource);

            var secret = (await service.GetSecurablesAsync(SecurableKind.Table, Schema, ct))
                .Single(t => t.Name == "secret");
            var columns = await service.GetColumnAclsAsync(secret, ct);

            // Only the granted column comes back: a NULL attacl is the normal
            // case and means the table's own ACL decides.
            await Assert.That(string.Join(",", columns.Select(c => c.Column))).IsEqualTo("id");
            await Assert.That(columns[0].Entries.Any(e => e.Grantee == Readers && e.Privilege == PrivilegeKind.Select)).IsTrue();

            var defaults = await service.GetDefaultPrivilegesAsync(ct);
            var ours = defaults.Single(d => d.Schema == Schema && d.AppliesTo == SecurableKind.Table);
            await Assert.That(ours.Entries.Any(e => e.Grantee == Readers && e.Privilege == PrivilegeKind.Select)).IsTrue();

            var rls = await service.GetRlsAsync(Schema, ct);

            var orders = rls.Single(t => t.Table == "orders");
            await Assert.That(orders.RowSecurityEnabled).IsTrue();
            await Assert.That(orders.Policies).Count().IsEqualTo(1);
            await Assert.That(orders.Policies[0].Command).IsEqualTo("SELECT");
            await Assert.That(string.Join(",", orders.Policies[0].Roles)).IsEqualTo(Readers);
            await Assert.That(orders.Policies[0].Using).IsNotNull();

            // A policy on a table where RLS was never switched on does nothing —
            // showing it is the point, since it looks like protection and is not.
            var inert = rls.Single(t => t.Table == "inert");
            await Assert.That(inert.HasInertPolicies).IsTrue();
        }
        finally
        {
            await DropFixtureAsync(dataSource);
        }
    }

    /// <summary>
    /// The end-to-end differentiator: the server's own answer, attributed to a
    /// source. app_ro holds SELECT only through readers, two memberships up.
    /// </summary>
    [Test]
    public async Task Effective_privileges_attribute_an_inherited_grant()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var ct = CancellationToken.None;
        await ResetFixtureAsync(dataSource);

        try
        {
            var roleService = new RoleService(dataSource);
            var privileges = new PrivilegeService(dataSource);
            var version = await roleService.GetServerVersionAsync(ct);

            var graph = RoleGraph.Build(
                await roleService.GetRolesAsync(includePredefined: true, ct),
                await roleService.GetMembershipsAsync(ct));

            var orders = (await privileges.GetSecurablesAsync(SecurableKind.Table, Schema, ct))
                .Single(t => t.Name == "orders");
            var acl = await privileges.GetAclAsync(orders, ct);

            // The privilege list must come from the real server version: asking a
            // pre-17 server about MAINTAIN raises "unrecognized privilege type"
            // and takes the whole matrix with it.
            var kinds = Privileges.For(SecurableKind.Table, version);
            string[] roles = [AppRo, Readers, Legacy];

            var answers = await privileges.GetServerAnswersAsync(orders, roles, kinds, ct);
            var effective = EffectivePrivilegeResolver.Resolve(acl, roles, kinds, graph, answers);

            var select = effective.Single(e => e.Role == AppRo && e.Privilege == PrivilegeKind.Select);
            await Assert.That(select.Granted).IsTrue();
            await Assert.That(select.Source).IsEqualTo(PrivilegeSource.Inherited);
            await Assert.That(select.Via).IsEqualTo(Readers);

            var insert = effective.Single(e => e.Role == AppRo && e.Privilege == PrivilegeKind.Insert);
            await Assert.That(insert.Granted).IsFalse();

            // legacy is a member of nothing and was granted nothing.
            var legacySelect = effective.Single(e => e.Role == Legacy && e.Privilege == PrivilegeKind.Select);
            await Assert.That(legacySelect.Granted).IsFalse();

            await Assert.That(await privileges.HasSchemaUsageAsync(AppRo, Schema, ct)).IsTrue();
        }
        finally
        {
            await DropFixtureAsync(dataSource);
        }
    }

    /// <summary>
    /// What a drop dialog shows before <c>DROP ROLE</c> fails with 2BP01 — the
    /// objects the role owns and the grants it holds.
    /// </summary>
    [Test]
    public async Task Role_dependencies_list_what_blocks_a_drop()
    {
        SkipIfNoConnection();
        await using var dataSource = CreateDataSource();
        var ct = CancellationToken.None;
        await ResetFixtureAsync(dataSource);

        try
        {
            await ExecuteAsync(dataSource, $"ALTER TABLE {Schema}.orders OWNER TO {Writers}");

            var service = new RoleService(dataSource);

            var owned = await service.GetOwnedObjectsAsync(Writers, ct);
            await Assert.That(owned.Any(d => d.Identity.Contains("orders", StringComparison.Ordinal))).IsTrue();

            var held = await service.GetGrantsHeldAsync(Readers, ct);
            await Assert.That(held).IsNotEmpty();
        }
        finally
        {
            await DropFixtureAsync(dataSource);
        }
    }
}
