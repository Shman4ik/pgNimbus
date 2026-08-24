using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// The generated script is the product here — it is what the user reads,
/// edits and runs — so these assert exact text rather than "contains GRANT".
/// A layout change has to be deliberate enough to update a test.
/// </summary>
public class GrantScriptBuilderTests
{
    private static SecurableRef Table(string schema, string name) =>
        new(SecurableKind.Table, 16384, schema, name);

    private static SecurableRef SchemaRef(string name) =>
        new(SecurableKind.Schema, 2200, null, name);

    /// <summary>Source files are CRLF; the builder emits "\n". Compare on one convention.</summary>
    private static string N(string text) => text.ReplaceLineEndings("\n");

    [Test]
    public async Task EmptyChangeSetProducesNoScript()
    {
        await Assert.That(GrantScriptBuilder.Build([])).IsEqualTo("");
    }

    [Test]
    public async Task PrivilegesForOneCellCollapseIntoOneStatement()
    {
        // Two clicks in the grid, one statement — and in Privileges.For order
        // (SELECT before INSERT), not the order they were clicked.
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(Table("sales", "orders"), "app_rw", PrivilegeKind.Insert, Grant: true),
            new PrivilegeChange(Table("sales", "orders"), "app_rw", PrivilegeKind.Select, Grant: true),
        ]);

        await Assert.That(N(sql)).IsEqualTo(N("""
            GRANT SELECT, INSERT ON TABLE sales.orders TO app_rw;
            """));
    }

    [Test]
    public async Task RevokesAreEmittedBeforeGrants()
    {
        // Grant-then-revoke and revoke-then-grant are different scripts. The
        // order is fixed by the builder, not by the click order.
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(Table("sales", "orders"), "app_rw", PrivilegeKind.Select, Grant: true),
            new PrivilegeChange(Table("sales", "orders"), "app_rw", PrivilegeKind.Delete, Grant: false),
        ]);

        await Assert.That(N(sql)).IsEqualTo(N("""
            REVOKE DELETE ON TABLE sales.orders FROM app_rw;
            GRANT SELECT ON TABLE sales.orders TO app_rw;
            """));
    }

    [Test]
    public async Task AFullPrivilegeSetCollapsesToAllPrivileges()
    {
        var everything = Privileges.For(SecurableKind.Table)
            .Select(p => new PrivilegeChange(Table("sales", "orders"), "app_rw", p, Grant: true))
            .ToList();

        await Assert.That(N(GrantScriptBuilder.Build(everything))).IsEqualTo(N("""
            GRANT ALL PRIVILEGES ON TABLE sales.orders TO app_rw;
            """));
    }

    [Test]
    public async Task APartialPrivilegeSetIsSpelledOut()
    {
        // One short of the full set must not become ALL PRIVILEGES — that would
        // widen the grant on any server whose "all" is bigger than ours.
        var almost = Privileges.For(SecurableKind.Table)
            .Where(p => p != PrivilegeKind.Truncate)
            .Select(p => new PrivilegeChange(Table("sales", "orders"), "app_rw", p, Grant: true))
            .ToList();

        await Assert.That(N(GrantScriptBuilder.Build(almost))).IsEqualTo(N("""
            GRANT SELECT, INSERT, UPDATE, DELETE, REFERENCES, TRIGGER, MAINTAIN ON TABLE sales.orders TO app_rw;
            """));
    }

    [Test]
    public async Task GrantOptionIsASuffixOnGrantAndAPrefixOnRevoke()
    {
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(Table("sales", "orders"), "app_rw", PrivilegeKind.Select, Grant: true, WithGrantOption: true),
            new PrivilegeChange(Table("sales", "orders"), "app_ro", PrivilegeKind.Select, Grant: false, WithGrantOption: true),
        ]);

        // REVOKE has no "WITH GRANT OPTION" tail; taking back only the right to
        // re-grant is GRANT OPTION FOR, a different statement.
        await Assert.That(N(sql)).IsEqualTo(N("""
            REVOKE GRANT OPTION FOR SELECT ON TABLE sales.orders FROM app_ro;
            GRANT SELECT ON TABLE sales.orders TO app_rw WITH GRANT OPTION;
            """));
    }

    [Test]
    public async Task PublicIsAKeywordOnBothSides()
    {
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(SchemaRef("sales"), null, PrivilegeKind.Usage, Grant: true),
            new PrivilegeChange(SchemaRef("archive"), null, PrivilegeKind.Usage, Grant: false),
            new PrivilegeChange(SchemaRef("archive"), null, PrivilegeKind.Create, Grant: false),
        ]);

        // PUBLIC is never quoted — "PUBLIC" would name a role instead.
        await Assert.That(N(sql)).IsEqualTo(N("""
            REVOKE ALL PRIVILEGES ON SCHEMA archive FROM PUBLIC;
            GRANT USAGE ON SCHEMA sales TO PUBLIC;
            """));
    }

    [Test]
    public async Task ColumnGrantsShareOneColumnList()
    {
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(Table("public", "users"), "app_ro", PrivilegeKind.Select, Grant: true, Column: "id"),
            new PrivilegeChange(Table("public", "users"), "app_ro", PrivilegeKind.Select, Grant: true, Column: "email"),
        ]);

        // Columns sorted ordinally, not by click order — the script has to be
        // byte-identical for the same change set.
        await Assert.That(N(sql)).IsEqualTo(N("""
            GRANT SELECT (email, id) ON TABLE public.users TO app_ro;
            """));
    }

    [Test]
    public async Task ColumnAndObjectGrantsCoexist()
    {
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(Table("public", "users"), "app_ro", PrivilegeKind.Update, Grant: true, Column: "email"),
            new PrivilegeChange(Table("public", "users"), "app_ro", PrivilegeKind.Select, Grant: true),
        ]);

        await Assert.That(N(sql)).IsEqualTo(N("""
            GRANT SELECT ON TABLE public.users TO app_ro;
            GRANT UPDATE (email) ON TABLE public.users TO app_ro;
            """));
    }

    [Test]
    public async Task IdentifiersAreQuotedOnlyWhenBeingBareWouldChangeThem()
    {
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(Table("sales", "Order Items"), "App RO", PrivilegeKind.Select, Grant: true),
            new PrivilegeChange(Table("sales", "orders"), "app_ro", PrivilegeKind.Select, Grant: true),
        ]);

        await Assert.That(N(sql)).IsEqualTo(N("""
            GRANT SELECT ON TABLE sales."Order Items" TO "App RO";
            GRANT SELECT ON TABLE sales.orders TO app_ro;
            """));
    }

    [Test]
    public async Task ShuffledInputProducesTheSameScript()
    {
        List<PrivilegeChange> changes =
        [
            new(Table("sales", "orders"), "app_rw", PrivilegeKind.Select, Grant: true),
            new(Table("sales", "orders"), "app_rw", PrivilegeKind.Insert, Grant: true),
            new(Table("archive", "orders"), null, PrivilegeKind.Select, Grant: false),
            new(SchemaRef("sales"), "app_rw", PrivilegeKind.Usage, Grant: true),
            new(Table("public", "users"), "app_ro", PrivilegeKind.Select, Grant: true, Column: "id"),
        ];

        var forwards = GrantScriptBuilder.Build(changes);
        var backwards = GrantScriptBuilder.Build([.. Enumerable.Reverse(changes)]);

        await Assert.That(backwards).IsEqualTo(forwards);
        await Assert.That(N(forwards)).IsEqualTo(N("""
            REVOKE SELECT ON TABLE archive.orders FROM PUBLIC;
            GRANT SELECT (id) ON TABLE public.users TO app_ro;
            GRANT USAGE ON SCHEMA sales TO app_rw;
            GRANT SELECT, INSERT ON TABLE sales.orders TO app_rw;
            """));
    }

    [Test]
    public async Task DuplicateChangesDoNotDuplicateAPrivilege()
    {
        var sql = GrantScriptBuilder.Build(
        [
            new PrivilegeChange(Table("sales", "orders"), "app_rw", PrivilegeKind.Select, Grant: true),
            new PrivilegeChange(Table("sales", "orders"), "app_rw", PrivilegeKind.Select, Grant: true),
        ]);

        await Assert.That(N(sql)).IsEqualTo(N("""
            GRANT SELECT ON TABLE sales.orders TO app_rw;
            """));
    }

    // ---- Bulk ----------------------------------------------------------

    [Test]
    public async Task ReadOnlyGrantsUsageFirstAndMentionsThePredefinedRole()
    {
        var sql = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            "sales", "app_ro", BulkGrantPreset.ReadOnly, IncludeFutureObjects: false, FutureObjectsOwner: null));

        await Assert.That(N(sql)).IsEqualTo(N("""
            -- GRANT ... ON ALL TABLES IN SCHEMA reaches only the objects that exist
            -- right now. Anything created afterwards is not covered by these statements.
            -- On PostgreSQL 14+ a cluster-wide read-only role is one membership instead
            -- of this whole script: GRANT pg_read_all_data TO app_ro; the role editor
            -- offers it.
            GRANT USAGE ON SCHEMA sales TO app_ro;
            GRANT SELECT ON ALL TABLES IN SCHEMA sales TO app_ro;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA sales TO app_ro;
            """));
    }

    [Test]
    public async Task ReadWriteWithFutureObjectsNamesTheCreatingRole()
    {
        var sql = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            "sales", "app_rw", BulkGrantPreset.ReadWrite, IncludeFutureObjects: true, FutureObjectsOwner: "app_owner"));

        await Assert.That(N(sql)).IsEqualTo(N("""
            -- GRANT ... ON ALL TABLES IN SCHEMA reaches only the objects that exist
            -- right now. Anything created afterwards is not covered by these statements.
            -- ALTER DEFAULT PRIVILEGES is keyed to the role that CREATES an object, not
            -- to the schema the object lands in. Set for the wrong creator it silently
            -- does nothing, which is why the owner has to be named.
            GRANT USAGE ON SCHEMA sales TO app_rw;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA sales TO app_rw;
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA sales TO app_rw;
            ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA sales GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_rw;
            ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA sales GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO app_rw;
            """));
    }

    [Test]
    public async Task FullAddsCreateOnTheSchemaAndCoversFunctions()
    {
        var sql = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            "sales", "app_full", BulkGrantPreset.Full, IncludeFutureObjects: false, FutureObjectsOwner: null));

        await Assert.That(N(sql)).IsEqualTo(N("""
            -- GRANT ... ON ALL TABLES IN SCHEMA reaches only the objects that exist
            -- right now. Anything created afterwards is not covered by these statements.
            GRANT USAGE ON SCHEMA sales TO app_full;
            GRANT CREATE ON SCHEMA sales TO app_full;
            GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA sales TO app_full;
            GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA sales TO app_full;
            GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA sales TO app_full;
            """));
    }

    [Test]
    public async Task RevokeAllReversesTheGrantScriptAndUndoesDefaultPrivileges()
    {
        var sql = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            "sales", "app_ro", BulkGrantPreset.RevokeAll, IncludeFutureObjects: true, FutureObjectsOwner: "app_owner"));

        await Assert.That(N(sql)).IsEqualTo(N("""
            -- REVOKE ... ON ALL TABLES IN SCHEMA reaches only the objects that exist
            -- right now. Anything created afterwards is not covered by these statements.
            -- ALTER DEFAULT PRIVILEGES is keyed to the role that CREATES an object, not
            -- to the schema the object lands in. Set for the wrong creator it silently
            -- does nothing, which is why the owner has to be named.
            ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA sales REVOKE ALL PRIVILEGES ON FUNCTIONS FROM app_ro;
            ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA sales REVOKE ALL PRIVILEGES ON SEQUENCES FROM app_ro;
            ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA sales REVOKE ALL PRIVILEGES ON TABLES FROM app_ro;
            REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA sales FROM app_ro;
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA sales FROM app_ro;
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA sales FROM app_ro;
            REVOKE ALL PRIVILEGES ON SCHEMA sales FROM app_ro;
            """));
    }

    [Test]
    [Arguments(BulkGrantPreset.ReadOnly)]
    [Arguments(BulkGrantPreset.ReadWrite)]
    [Arguments(BulkGrantPreset.Full)]
    public async Task UsageOnTheSchemaIsAlwaysTheFirstStatement(BulkGrantPreset preset)
    {
        // pgadmin4#8954: granting every table privilege without the schema's own
        // USAGE leaves the user with permission denied. It goes first, always.
        var sql = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            "sales", "app", preset, IncludeFutureObjects: true, FutureObjectsOwner: "app_owner"));

        var firstStatement = N(sql).Split('\n').First(l => !l.StartsWith("--", StringComparison.Ordinal));

        await Assert.That(firstStatement).IsEqualTo("GRANT USAGE ON SCHEMA sales TO app;");
    }

    [Test]
    public async Task FutureObjectsWithoutAnOwnerSaysSoInsteadOfGuessing()
    {
        var sql = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            "sales", "app_ro", BulkGrantPreset.ReadOnly, IncludeFutureObjects: true, FutureObjectsOwner: null));

        await Assert.That(sql).Contains("-- No creating role was named, so the ALTER DEFAULT PRIVILEGES statements are");
        await Assert.That(sql).DoesNotContain("ALTER DEFAULT PRIVILEGES FOR ROLE");
    }

    [Test]
    public async Task BulkQuotesTheSchemaAndGranteeWhenTheyNeedIt()
    {
        var sql = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            "Sales Archive", null, BulkGrantPreset.ReadOnly, IncludeFutureObjects: false, FutureObjectsOwner: null));

        await Assert.That(sql).Contains("""GRANT USAGE ON SCHEMA "Sales Archive" TO PUBLIC;""");
    }
}
