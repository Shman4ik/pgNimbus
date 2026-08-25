using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

public class RoleScriptBuilderTests
{
    private static readonly DateTimeOffset Expiry = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Source files are CRLF; the builder emits "\n". Compare on one convention.</summary>
    private static string N(string text) => text.ReplaceLineEndings("\n");

    private static RoleDefinition Definition(
        string name = "app_rw",
        bool canLogin = true,
        bool isSuperuser = false,
        bool inherit = true,
        bool canCreateDb = false,
        bool canCreateRole = false,
        bool canReplicate = false,
        bool bypassRls = false,
        int? connectionLimit = null,
        DateTimeOffset? validUntil = null,
        IReadOnlyList<string>? memberOf = null,
        string? comment = null) =>
        new(name, canLogin, isSuperuser, inherit, canCreateDb, canCreateRole, canReplicate, bypassRls,
            connectionLimit, validUntil, memberOf ?? [], comment);

    private static RoleAttributes Attributes(
        string name = "app_rw",
        bool canLogin = true,
        bool isSuperuser = false,
        bool inherit = true,
        bool canCreateRole = false,
        bool canCreateDb = false,
        bool canReplicate = false,
        bool bypassRls = false,
        int connectionLimit = -1,
        DateTimeOffset? validUntil = null,
        string? comment = null) =>
        new(16385, name, canLogin, isSuperuser, inherit, canCreateRole, canCreateDb, canReplicate, bypassRls,
            connectionLimit, validUntil, [], comment);

    [Test]
    public async Task CreateSpellsOutEveryNegativeKeyword()
    {
        // No attribute is left to the server's default: the script has to say
        // the same thing wherever it is pasted.
        var sql = RoleScriptBuilder.Create(Definition(), password: null);

        await Assert.That(N(sql)).IsEqualTo(N("""
            CREATE ROLE app_rw WITH LOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            """));
    }

    [Test]
    public async Task CreateWithEverythingSet()
    {
        var sql = RoleScriptBuilder.Create(
            Definition(
                connectionLimit: 10,
                validUntil: Expiry,
                memberOf: ["readers", "Report Writers"],
                comment: "the application role"),
            password: "hunter2");

        await Assert.That(N(sql)).IsEqualTo(N("""
            CREATE ROLE app_rw WITH LOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 10 VALID UNTIL '2027-01-01 00:00:00+00:00' PASSWORD 'hunter2';
            GRANT readers TO app_rw;
            GRANT "Report Writers" TO app_rw;
            COMMENT ON ROLE app_rw IS 'the application role';
            """));
    }

    [Test]
    public async Task MaskedCreateNeverEmitsTheRealPassword()
    {
        // The same call renders the preview and the executed statement; only
        // this flag differs. A leaked literal here is a leaked credential.
        var sql = RoleScriptBuilder.Create(Definition(), password: "hunter2", maskPassword: true);

        await Assert.That(sql).DoesNotContain("hunter2");
        await Assert.That(sql).Contains("PASSWORD '••••';");
    }

    [Test]
    public async Task CreateQuotesANameThatNeedsIt()
    {
        var sql = RoleScriptBuilder.Create(Definition(name: "App Reader"), password: null);

        await Assert.That(sql).StartsWith("""CREATE ROLE "App Reader" WITH """);
    }

    [Test]
    public async Task CreateEscapesAQuoteInTheComment()
    {
        var sql = RoleScriptBuilder.Create(Definition(comment: "the app's role"), password: null);

        await Assert.That(sql).Contains("COMMENT ON ROLE app_rw IS 'the app''s role';");
    }

    [Test]
    public async Task AlterEmitsNothingWhenNothingChanged()
    {
        await Assert.That(RoleScriptBuilder.Alter(Attributes(), Definition())).IsEqualTo("");
    }

    [Test]
    public async Task AlterEmitsOnlyTheChangedAttributes()
    {
        var sql = RoleScriptBuilder.Alter(
            Attributes(canLogin: true, canCreateDb: false),
            Definition(canLogin: false, canCreateDb: true));

        await Assert.That(N(sql)).IsEqualTo(N("""
            ALTER ROLE app_rw WITH NOLOGIN CREATEDB;
            """));
    }

    [Test]
    public async Task AlterTreatsANullConnectionLimitAsRemovingTheLimit()
    {
        // Postgres stores "no limit" as -1, so clearing a limit is a real change.
        var sql = RoleScriptBuilder.Alter(Attributes(connectionLimit: 5), Definition(connectionLimit: null));

        await Assert.That(N(sql)).IsEqualTo(N("""
            ALTER ROLE app_rw WITH CONNECTION LIMIT -1;
            """));
    }

    [Test]
    public async Task AlterClearsAnExpiryWithInfinity()
    {
        // There is no NO VALID UNTIL; 'infinity' is how the expiry is removed.
        var sql = RoleScriptBuilder.Alter(Attributes(validUntil: Expiry), Definition(validUntil: null));

        await Assert.That(N(sql)).IsEqualTo(N("""
            ALTER ROLE app_rw WITH VALID UNTIL 'infinity';
            """));
    }

    [Test]
    public async Task AlterDiffsMembershipsWhenTheCurrentOnesAreKnown()
    {
        var sql = RoleScriptBuilder.Alter(
            Attributes(),
            Definition(memberOf: ["readers", "auditors"]),
            currentMemberOf: ["readers", "writers"]);

        await Assert.That(N(sql)).IsEqualTo(N("""
            GRANT auditors TO app_rw;
            REVOKE writers FROM app_rw;
            """));
    }

    [Test]
    public async Task AlterLeavesMembershipAloneWhenTheCurrentOnesAreUnknown()
    {
        // RoleAttributes carries no memberships (they live in pg_auth_members,
        // not pg_roles). Unknown means untouched, not "assume none".
        var sql = RoleScriptBuilder.Alter(Attributes(), Definition(memberOf: ["readers"]));

        await Assert.That(sql).IsEqualTo("");
    }

    [Test]
    public async Task AlterEmitsACommentChangeAndClearsItWithNull()
    {
        await Assert.That(RoleScriptBuilder.Alter(Attributes(comment: "old"), Definition(comment: "new")))
            .IsEqualTo("COMMENT ON ROLE app_rw IS 'new';");

        await Assert.That(RoleScriptBuilder.Alter(Attributes(comment: "old"), Definition(comment: null)))
            .IsEqualTo("COMMENT ON ROLE app_rw IS NULL;");
    }

    [Test]
    public async Task SetPasswordMasksOnRequest()
    {
        await Assert.That(RoleScriptBuilder.SetPassword("app_rw", "hunter2"))
            .IsEqualTo("ALTER ROLE app_rw WITH PASSWORD 'hunter2';");

        await Assert.That(RoleScriptBuilder.SetPassword("app_rw", "hunter2", maskPassword: true))
            .IsEqualTo("ALTER ROLE app_rw WITH PASSWORD '••••';");
    }

    [Test]
    public async Task RenameAndMembershipHelpers()
    {
        await Assert.That(RoleScriptBuilder.Rename("app", "App Reader"))
            .IsEqualTo("""ALTER ROLE app RENAME TO "App Reader";""");
        await Assert.That(RoleScriptBuilder.AddMember("readers", "app")).IsEqualTo("GRANT readers TO app;");
        await Assert.That(RoleScriptBuilder.RemoveMember("readers", "app")).IsEqualTo("REVOKE readers FROM app;");
    }

    [Test]
    public async Task DropReassignsBeforeDroppingOwnedAndThenTheRole()
    {
        // The order is the whole point: REASSIGN OWNED moves the objects,
        // DROP OWNED clears what is left, DROP ROLE then succeeds.
        var sql = RoleScriptBuilder.Drop("app", "postgres");

        await Assert.That(N(sql)).IsEqualTo(N("""
            -- "app" may own objects or hold grants, and DROP ROLE alone
            -- then fails with 2BP01 without naming either the objects or the fix.
            -- REASSIGN OWNED hands the objects to another role; DROP OWNED then removes
            -- the privileges REASSIGN OWNED leaves behind. Both act on the current
            -- database only -- repeat them while connected to every other database that
            -- contains objects owned by this role, then drop it.
            REASSIGN OWNED BY app TO postgres;
            DROP OWNED BY app;
            DROP ROLE app;
            """));
    }

    [Test]
    public async Task DropWithoutAReassignTargetWarnsThatObjectsAreDeleted()
    {
        var sql = RoleScriptBuilder.Drop("app", reassignTo: null);

        await Assert.That(N(sql)).IsEqualTo(N("""
            -- "app" may own objects or hold grants, and DROP ROLE alone
            -- then fails with 2BP01 without naming either the objects or the fix.
            -- No role was named to take the objects over, so DROP OWNED will DELETE
            -- everything "app" owns -- tables, schemas, data -- rather than hand
            -- it over. Name a role to reassign to first if the objects should be kept.
            -- DROP OWNED acts on the current database only -- repeat it while connected
            -- to every other database that contains objects owned by this role, then
            -- drop it.
            DROP OWNED BY app;
            DROP ROLE app;
            """));
    }

    /// <summary>
    /// The managed-Postgres path. REASSIGN OWNED and DROP OWNED need the
    /// privileges of the role being emptied, and nobody on RDS/Neon/Supabase is
    /// a superuser holding them implicitly — without the grants the server
    /// answers 42501 rather than doing anything.
    /// </summary>
    [Test]
    public async Task DropCanGrantItselfTheMembershipsItNeeds()
    {
        var sql = RoleScriptBuilder.Drop("app", "postgres", grantMembershipFirst: true);

        // Both grants come before REASSIGN, and the one that outlives the script
        // — membership in the target — is handed back before the role is dropped.
        await Assert.That(N(sql)).IsEqualTo(N("""
            -- "app" may own objects or hold grants, and DROP ROLE alone
            -- then fails with 2BP01 without naming either the objects or the fix.
            -- REASSIGN OWNED and DROP OWNED require the privileges of the role being
            -- emptied. Without them the server answers 42501, however many objects the
            -- role owns. A superuser holds them implicitly; otherwise grant the
            -- membership to yourself first -- it is revoked again below.
            GRANT app TO CURRENT_USER;
            GRANT postgres TO CURRENT_USER;
            -- REASSIGN OWNED hands the objects to another role; DROP OWNED then removes
            -- the privileges REASSIGN OWNED leaves behind. Both act on the current
            -- database only -- repeat them while connected to every other database that
            -- contains objects owned by this role, then drop it.
            REASSIGN OWNED BY app TO postgres;
            DROP OWNED BY app;
            REVOKE postgres FROM CURRENT_USER;
            DROP ROLE app;
            """));
    }

    /// <summary>
    /// With nobody to reassign to there is no second membership to grant, and
    /// nothing to hand back — DROP ROLE takes the membership in the dropped role
    /// with it.
    /// </summary>
    [Test]
    public async Task DropWithoutAReassignTargetGrantsAndRevokesNothingExtra()
    {
        var sql = RoleScriptBuilder.Drop("app", reassignTo: null, grantMembershipFirst: true);

        await Assert.That(sql).Contains("GRANT app TO CURRENT_USER;");
        await Assert.That(sql).DoesNotContain("REVOKE");
        await Assert.That(sql).DoesNotContain("REASSIGN OWNED BY");

        // The explanation names only the statement the script actually runs.
        await Assert.That(sql).Contains("-- DROP OWNED requires the privileges");
    }

    [Test]
    public async Task DropQuotesTheRoleInEveryStatement()
    {
        var sql = RoleScriptBuilder.Drop("App Reader", "postgres");

        await Assert.That(sql).Contains("""REASSIGN OWNED BY "App Reader" TO postgres;""");
        await Assert.That(sql).Contains("""DROP OWNED BY "App Reader";""");
        await Assert.That(sql).Contains("""DROP ROLE "App Reader";""");
    }

    [Test]
    public async Task DropCannotBeBrokenOutOfByANewlineInTheRoleName()
    {
        // A role name can legally contain a newline, and the recipe comment is
        // the one place a name is not inside a quoted identifier.
        var sql = RoleScriptBuilder.Drop("ap\np", "postgres");

        var commentLines = N(sql).Split('\n').TakeWhile(l => l.StartsWith("--", StringComparison.Ordinal));

        await Assert.That(commentLines.Count()).IsEqualTo(6);
    }
}
