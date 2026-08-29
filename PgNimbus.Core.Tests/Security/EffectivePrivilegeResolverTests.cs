using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

public class EffectivePrivilegeResolverTests
{
    // ------------------------------------------------------------- fixtures

    /// <summary>
    /// A hand-fed <see cref="IRoleMembershipLookup"/> so the resolver can be
    /// exercised without a server (and without RoleGraph, which is the thing
    /// under test's collaborator, not its dependency).
    /// </summary>
    private sealed class FakeLookup(
        Dictionary<string, IReadOnlyList<string>>? groups = null,
        params string[] superusers) : IRoleMembershipLookup
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _groups = groups ?? [];
        private readonly HashSet<string> _superusers = new HashSet<string>(superusers, StringComparer.Ordinal);

        /// <summary>Nearest first, as the interface documents.</summary>
        public IReadOnlyList<string> InheritedGroups(string role) =>
            _groups.TryGetValue(role, out var g) ? g : [];

        public bool IsSuperuser(string role) => _superusers.Contains(role);
    }

    private static SecurableRef Table(string schema = "sales", string name = "orders") =>
        new(SecurableKind.Table, 16384, schema, name);

    private static SecurableRef Function() =>
        new(SecurableKind.Function, 16500, "public", "f", "integer");

    private static SecurableRef Database() =>
        new(SecurableKind.Database, 16600, null, "demo");

    private static ObjectAcl Acl(SecurableRef obj, string owner, params AclEntry[] entries) =>
        new(obj, owner, false, entries);

    /// <summary>The catalog ACL column was NULL: no entries, but the defaults apply.</summary>
    private static ObjectAcl DefaultAcl(SecurableRef obj, string owner) =>
        new(obj, owner, true, []);

    private static AclEntry Grant(
        string? grantee,
        PrivilegeKind privilege,
        string? grantor = "postgres",
        bool withGrantOption = false) =>
        new(grantee, grantor, privilege, withGrantOption);

    private static readonly IReadOnlyList<PrivilegeKind> Crud =
    [
        PrivilegeKind.Select, PrivilegeKind.Insert, PrivilegeKind.Update, PrivilegeKind.Delete,
    ];

    private static EffectivePrivilege One(IReadOnlyList<EffectivePrivilege> all, PrivilegeKind privilege) =>
        all.Single(e => e.Privilege == privilege);

    // ------------------------------------------------------------ resolution

    [Test]
    public async Task DirectGrantIsAttributedToItsGrantor()
    {
        var acl = Acl(Table(), "postgres", Grant("app_ro", PrivilegeKind.Select));

        var result = EffectivePrivilegeResolver.Resolve(
            acl, ["app_ro"], Crud, new FakeLookup());

        var select = One(result, PrivilegeKind.Select);
        await Assert.That(select.Granted).IsTrue();
        await Assert.That(select.Source).IsEqualTo(PrivilegeSource.Direct);
        await Assert.That(select.GrantedBy).IsEqualTo("postgres");
        await Assert.That(select.Via).IsNull();

        // And the ones nobody granted stay honestly empty.
        await Assert.That(One(result, PrivilegeKind.Insert).Granted).IsFalse();
        await Assert.That(One(result, PrivilegeKind.Insert).Source).IsEqualTo(PrivilegeSource.None);
    }

    [Test]
    public async Task ResolveCoversEveryRoleTimesPrivilege()
    {
        var result = EffectivePrivilegeResolver.Resolve(
            Acl(Table(), "postgres"), ["a", "b", "c"], Crud, new FakeLookup());

        await Assert.That(result).Count().IsEqualTo(12);
    }

    [Test]
    public async Task GrantToAGroupIsInheritedByItsMember()
    {
        // The case every other tool renders as "app_ro has nothing".
        var acl = Acl(Table(), "postgres", Grant("readers", PrivilegeKind.Select));
        var lookup = new FakeLookup(new() { ["app_ro"] = new[] { "readers" } });

        var select = One(
            EffectivePrivilegeResolver.Resolve(acl, ["app_ro"], [PrivilegeKind.Select], lookup),
            PrivilegeKind.Select);

        await Assert.That(select.Granted).IsTrue();
        await Assert.That(select.Source).IsEqualTo(PrivilegeSource.Inherited);
        await Assert.That(select.Via).IsEqualTo("readers");
        await Assert.That(select.GrantedBy).IsEqualTo("postgres");
    }

    [Test]
    public async Task NearestGroupWinsWhenTwoInTheChainHaveTheGrant()
    {
        // app_ro -> readers -> everyone, and both hold SELECT. The attribution
        // must name readers: it is the edge the user would actually edit.
        // Deliberately ordered so the *far* group's ACL entry comes first, which
        // is what a naive scan of acl.Entries would pick up.
        var acl = Acl(
            Table(), "postgres",
            Grant("everyone", PrivilegeKind.Select),
            Grant("readers", PrivilegeKind.Select));
        var lookup = new FakeLookup(new() { ["app_ro"] = new[] { "readers", "everyone" } });

        var select = One(
            EffectivePrivilegeResolver.Resolve(acl, ["app_ro"], [PrivilegeKind.Select], lookup),
            PrivilegeKind.Select);

        await Assert.That(select.Source).IsEqualTo(PrivilegeSource.Inherited);
        await Assert.That(select.Via).IsEqualTo("readers");
    }

    [Test]
    public async Task PublicGrantReachesARoleWithNoGrantsOfItsOwn()
    {
        var acl = Acl(Table(), "postgres", Grant(null, PrivilegeKind.Select));

        var select = One(
            EffectivePrivilegeResolver.Resolve(acl, ["nobody"], [PrivilegeKind.Select], new FakeLookup()),
            PrivilegeKind.Select);

        await Assert.That(select.Granted).IsTrue();
        await Assert.That(select.Source).IsEqualTo(PrivilegeSource.Public);
    }

    [Test]
    public async Task OwnerHoldsEverythingWithNoAclEntryAtAll()
    {
        var result = EffectivePrivilegeResolver.Resolve(
            Acl(Table(), "alice"), ["alice"], Crud, new FakeLookup());

        await Assert.That(result.All(e => e.Granted)).IsTrue();
        await Assert.That(result.All(e => e.Source == PrivilegeSource.Owner)).IsTrue();
    }

    [Test]
    public async Task OwnershipOutranksAnExplicitDirectGrant()
    {
        // A GRANT naming the owner is redundant; reporting it as "granted
        // directly" would send someone off to revoke a grant that changes nothing.
        var acl = Acl(Table(), "alice", Grant("alice", PrivilegeKind.Select));

        var select = One(
            EffectivePrivilegeResolver.Resolve(acl, ["alice"], [PrivilegeKind.Select], new FakeLookup()),
            PrivilegeKind.Select);

        await Assert.That(select.Source).IsEqualTo(PrivilegeSource.Owner);
    }

    [Test]
    public async Task SuperuserOutranksOwnershipAndEveryGrant()
    {
        var acl = Acl(Table(), "postgres", Grant("postgres", PrivilegeKind.Select));
        var lookup = new FakeLookup(superusers: "postgres");

        var result = EffectivePrivilegeResolver.Resolve(acl, ["postgres"], Crud, lookup);

        await Assert.That(result.All(e => e.Granted)).IsTrue();
        await Assert.That(result.All(e => e.Source == PrivilegeSource.Superuser)).IsTrue();
    }

    [Test]
    public async Task WithGrantOptionIsCarriedThrough()
    {
        var acl = Acl(
            Table(), "postgres",
            Grant("app_rw", PrivilegeKind.Select, withGrantOption: true),
            Grant("app_rw", PrivilegeKind.Insert));

        var result = EffectivePrivilegeResolver.Resolve(
            acl, ["app_rw"], [PrivilegeKind.Select, PrivilegeKind.Insert], new FakeLookup());

        await Assert.That(One(result, PrivilegeKind.Select).WithGrantOption).IsTrue();
        await Assert.That(One(result, PrivilegeKind.Insert).WithGrantOption).IsFalse();
    }

    // -------------------------------------------------------- NULL ACL column

    [Test]
    public async Task DefaultAclOnATableGrantsPublicNothing()
    {
        // relacl IS NULL means "the owner has everything and the defaults
        // apply" — and for a table the default for PUBLIC is nothing at all.
        var acl = DefaultAcl(Table(), "alice");

        var result = EffectivePrivilegeResolver.Resolve(acl, ["bob"], Crud, new FakeLookup());

        await Assert.That(result.All(e => !e.Granted)).IsTrue();
        await Assert.That(result.All(e => e.Source == PrivilegeSource.None)).IsTrue();
    }

    [Test]
    public async Task DefaultAclOnATableStillGivesTheOwnerEverything()
    {
        var result = EffectivePrivilegeResolver.Resolve(
            DefaultAcl(Table(), "alice"), ["alice"], Crud, new FakeLookup());

        await Assert.That(result.All(e => e.Source == PrivilegeSource.Owner)).IsTrue();
    }

    [Test]
    public async Task DefaultAclOnAFunctionGrantsPublicExecute()
    {
        // proacl IS NULL: Postgres executes this for anybody. A grid that
        // rendered the empty ACL literally would claim the opposite.
        var result = EffectivePrivilegeResolver.Resolve(
            DefaultAcl(Function(), "alice"), ["bob"], [PrivilegeKind.Execute], new FakeLookup());

        await Assert.That(result[0].Granted).IsTrue();
        await Assert.That(result[0].Source).IsEqualTo(PrivilegeSource.Public);
    }

    [Test]
    public async Task DefaultAclOnADatabaseGrantsPublicConnectAndTemporary()
    {
        var result = EffectivePrivilegeResolver.Resolve(
            DefaultAcl(Database(), "alice"),
            ["bob"],
            [PrivilegeKind.Connect, PrivilegeKind.Temporary, PrivilegeKind.Create],
            new FakeLookup());

        await Assert.That(One(result, PrivilegeKind.Connect).Source).IsEqualTo(PrivilegeSource.Public);
        await Assert.That(One(result, PrivilegeKind.Temporary).Source).IsEqualTo(PrivilegeSource.Public);

        // CREATE is not a default — only the owner gets it.
        await Assert.That(One(result, PrivilegeKind.Create).Granted).IsFalse();
    }

    [Test]
    public async Task AnExplicitlyEmptyAclIsNotADefaultOne()
    {
        // relacl = '{}' (everything revoked, even from the owner's own default
        // entry) is a different state from relacl IS NULL, and PUBLIC gets
        // nothing on a function whose ACL was explicitly emptied.
        var acl = Acl(Function(), "alice");

        var result = EffectivePrivilegeResolver.Resolve(
            acl, ["bob"], [PrivilegeKind.Execute], new FakeLookup());

        await Assert.That(result[0].Granted).IsFalse();
    }

    // ------------------------------------------------------- reconciliation

    [Test]
    public async Task ServerAgreementKeepsTheResolvedSource()
    {
        var acl = Acl(Table(), "postgres", Grant("app_ro", PrivilegeKind.Select));

        var result = EffectivePrivilegeResolver.Resolve(
            acl, ["app_ro"], [PrivilegeKind.Select], new FakeLookup(),
            new Dictionary<(string, PrivilegeKind), bool> { [("app_ro", PrivilegeKind.Select)] = true });

        await Assert.That(result[0].Granted).IsTrue();
        await Assert.That(result[0].Source).IsEqualTo(PrivilegeSource.Direct);
    }

    [Test]
    public async Task ServerSaysGrantedButNothingWeReadExplainsIt()
    {
        // A grant through a role we could not see. Say so, don't guess.
        var result = EffectivePrivilegeResolver.Resolve(
            Acl(Table(), "postgres"), ["app_ro"], [PrivilegeKind.Select], new FakeLookup(),
            new Dictionary<(string, PrivilegeKind), bool> { [("app_ro", PrivilegeKind.Select)] = true });

        await Assert.That(result[0].Granted).IsTrue();
        await Assert.That(result[0].Source).IsEqualTo(PrivilegeSource.Unknown);
        await Assert.That(result[0].Explanation).IsEqualTo("granted, source not visible from here");
    }

    [Test]
    public async Task ServerSaysDeniedSoTheResolvedGrantIsDropped()
    {
        // The NOINHERIT case: the ACL names a group the role belongs to, but the
        // privilege only arrives after SET ROLE, so the server says no.
        var acl = Acl(Table(), "postgres", Grant("readers", PrivilegeKind.Select));
        var lookup = new FakeLookup(new() { ["app_ro"] = new[] { "readers" } });

        var result = EffectivePrivilegeResolver.Resolve(
            acl, ["app_ro"], [PrivilegeKind.Select], lookup,
            new Dictionary<(string, PrivilegeKind), bool> { [("app_ro", PrivilegeKind.Select)] = false });

        await Assert.That(result[0].Granted).IsFalse();
        await Assert.That(result[0].Source).IsEqualTo(PrivilegeSource.None);
        await Assert.That(result[0].Via).IsNull();
    }

    [Test]
    public async Task PairsTheServerDidNotAnswerFallBackToTheResolver()
    {
        var acl = Acl(Table(), "postgres", Grant("app_ro", PrivilegeKind.Select));

        var result = EffectivePrivilegeResolver.Resolve(
            acl, ["app_ro"], [PrivilegeKind.Select, PrivilegeKind.Insert], new FakeLookup(),
            new Dictionary<(string, PrivilegeKind), bool> { [("app_ro", PrivilegeKind.Insert)] = false });

        await Assert.That(One(result, PrivilegeKind.Select).Source).IsEqualTo(PrivilegeSource.Direct);
        await Assert.That(One(result, PrivilegeKind.Insert).Granted).IsFalse();
    }

    // ------------------------------------------------------------- sentence

    [Test]
    public async Task ExplainSentenceNamesTheSourceThenWhatIsMissing()
    {
        var acl = Acl(Table(), "postgres", Grant("readers", PrivilegeKind.Select));
        var lookup = new FakeLookup(new() { ["app_ro"] = new[] { "readers" } });
        var effective = EffectivePrivilegeResolver.Resolve(acl, ["app_ro"], Crud, lookup);

        var sentence = EffectivePrivilegeResolver.ExplainSentence(
            "app_ro", Table(), effective, hasSchemaUsage: true);

        await Assert.That(sentence).IsEqualTo(
            "app_ro can SELECT sales.orders — inherited from readers. It cannot INSERT, UPDATE or DELETE.");
    }

    [Test]
    public async Task ExplainSentenceListsSeveralGrantedPrivileges()
    {
        var acl = Acl(
            Table(), "postgres",
            Grant("app_rw", PrivilegeKind.Select),
            Grant("app_rw", PrivilegeKind.Insert));
        var effective = EffectivePrivilegeResolver.Resolve(acl, ["app_rw"], Crud, new FakeLookup());

        var sentence = EffectivePrivilegeResolver.ExplainSentence(
            "app_rw", Table(), effective, hasSchemaUsage: true);

        await Assert.That(sentence).IsEqualTo(
            "app_rw can SELECT and INSERT sales.orders — granted directly by postgres. "
            + "It cannot UPDATE or DELETE.");
    }

    [Test]
    public async Task ExplainSentenceSaysSoWhenNothingIsGranted()
    {
        var effective = EffectivePrivilegeResolver.Resolve(
            Acl(Table(), "postgres"), ["app_ro"], Crud, new FakeLookup());

        var sentence = EffectivePrivilegeResolver.ExplainSentence(
            "app_ro", Table(), effective, hasSchemaUsage: true);

        await Assert.That(sentence).IsEqualTo("app_ro has no privileges on sales.orders.");
    }

    [Test]
    public async Task ExplainSentenceCallsOutTheMissingSchemaUsage()
    {
        // Every table privilege granted, and it still fails. This is the trap.
        var acl = Acl(
            Table(), "postgres",
            Grant("app_ro", PrivilegeKind.Select),
            Grant("app_ro", PrivilegeKind.Insert),
            Grant("app_ro", PrivilegeKind.Update),
            Grant("app_ro", PrivilegeKind.Delete));
        var effective = EffectivePrivilegeResolver.Resolve(acl, ["app_ro"], Crud, new FakeLookup());

        var sentence = EffectivePrivilegeResolver.ExplainSentence(
            "app_ro", Table(), effective, hasSchemaUsage: false);

        await Assert.That(sentence).IsEqualTo(
            "app_ro can SELECT, INSERT, UPDATE and DELETE sales.orders — granted directly by postgres. "
            + "app_ro also lacks USAGE on schema sales, which blocks access to everything in it.");
    }

    [Test]
    public async Task ExplainSentenceIgnoresOtherRolesRows()
    {
        var acl = Acl(Table(), "postgres", Grant("someone_else", PrivilegeKind.Select));
        var effective = EffectivePrivilegeResolver.Resolve(
            acl, ["someone_else", "app_ro"], [PrivilegeKind.Select], new FakeLookup());

        var sentence = EffectivePrivilegeResolver.ExplainSentence(
            "app_ro", Table(), effective, hasSchemaUsage: true);

        await Assert.That(sentence).IsEqualTo("app_ro has no privileges on sales.orders.");
    }

    [Test]
    public async Task ExplainSentenceOnASchemalessObjectSkipsTheUsageClause()
    {
        // A database has no schema to need USAGE on, so the clause must not appear
        // even when the flag is false.
        var effective = EffectivePrivilegeResolver.Resolve(
            DefaultAcl(Database(), "alice"), ["bob"], [PrivilegeKind.Connect], new FakeLookup());

        var sentence = EffectivePrivilegeResolver.ExplainSentence(
            "bob", Database(), effective, hasSchemaUsage: false);

        await Assert.That(sentence).IsEqualTo("bob can CONNECT demo — granted to PUBLIC.");
    }
}
