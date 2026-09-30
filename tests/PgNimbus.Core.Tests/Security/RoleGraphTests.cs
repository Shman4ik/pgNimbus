using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

public class RoleGraphTests
{
    private static RoleAttributes Role(string name, bool superuser = false) =>
        new(0, name, CanLogin: true, IsSuperuser: superuser, Inherit: true, CanCreateRole: false,
            CanCreateDb: false, CanReplicate: false, BypassRls: false, ConnectionLimit: -1,
            ValidUntil: null, Settings: [], Comment: null);

    /// <summary><paramref name="member"/> is a member of <paramref name="group"/>.</summary>
    private static RoleMembership Edge(string member, string group, bool inherit = true) =>
        new(member, group, AdminOption: false, InheritOption: inherit, SetOption: true, Grantor: "postgres");

    [Test]
    public async Task EmptyInputHasNothingInIt()
    {
        var graph = RoleGraph.Build([], []);

        await Assert.That(graph.Roles).IsEmpty();
        await Assert.That(graph.MemberOf("anyone")).IsEmpty();
        await Assert.That(graph.Members("anyone")).IsEmpty();
        await Assert.That(graph.InheritedGroups("anyone")).IsEmpty();
    }

    [Test]
    public async Task SingleMembershipIsVisibleFromBothEnds()
    {
        var graph = RoleGraph.Build(
            [Role("app"), Role("readers")],
            [Edge("app", "readers")]);

        var memberOf = graph.MemberOf("app");
        await Assert.That(memberOf).Count().IsEqualTo(1);
        await Assert.That(memberOf[0].Role).IsEqualTo("readers");
        await Assert.That(memberOf[0].Inherits).IsTrue();
        await Assert.That(memberOf[0].Children).IsEmpty();

        var members = graph.Members("readers");
        await Assert.That(members).Count().IsEqualTo(1);
        await Assert.That(members[0].Role).IsEqualTo("app");

        await Assert.That(string.Join('|', graph.InheritedGroups("app"))).IsEqualTo("readers");
    }

    [Test]
    public async Task ChainIsReportedNearestFirst()
    {
        // app -> readers -> analysts: app holds everything granted to either group.
        var graph = RoleGraph.Build(
            [Role("app"), Role("readers"), Role("analysts")],
            [Edge("app", "readers"), Edge("readers", "analysts")]);

        await Assert.That(string.Join('|', graph.InheritedGroups("app"))).IsEqualTo("readers|analysts");

        var memberOf = graph.MemberOf("app");
        await Assert.That(memberOf.Single().Role).IsEqualTo("readers");
        await Assert.That(memberOf.Single().Children.Single().Role).IsEqualTo("analysts");
    }

    [Test]
    public async Task DiamondReportsTheSharedGroupOnce()
    {
        // app is in readers and writers; both are in staff. staff must not be listed twice.
        var graph = RoleGraph.Build(
            [Role("app"), Role("readers"), Role("writers"), Role("staff")],
            [Edge("app", "readers"), Edge("app", "writers"), Edge("readers", "staff"), Edge("writers", "staff")]);

        // Nearest level first, alphabetical within a level.
        await Assert.That(string.Join('|', graph.InheritedGroups("app")))
            .IsEqualTo("readers|writers|staff");
    }

    [Test]
    public async Task NoInheritGroupIsShownButNotClaimed()
    {
        // Postgres gives app the privileges of ops only after SET ROLE ops, so
        // reporting ops as inherited would promise access the server refuses.
        var graph = RoleGraph.Build(
            [Role("app"), Role("ops")],
            [Edge("app", "ops", inherit: false)]);

        await Assert.That(graph.InheritedGroups("app")).IsEmpty();

        // The relationship still exists and the tree has to show it.
        var memberOf = graph.MemberOf("app");
        await Assert.That(memberOf).Count().IsEqualTo(1);
        await Assert.That(memberOf[0].Role).IsEqualTo("ops");
        await Assert.That(memberOf[0].Inherits).IsFalse();
    }

    [Test]
    public async Task NoInheritInTheMiddleCutsOffEverythingAbove()
    {
        // app -> readers (inherit) -> ops (NOINHERIT) -> admins.
        // Nothing above the NOINHERIT edge reaches app, not even transitively.
        var graph = RoleGraph.Build(
            [Role("app"), Role("readers"), Role("ops"), Role("admins")],
            [Edge("app", "readers"), Edge("readers", "ops", inherit: false), Edge("ops", "admins")]);

        await Assert.That(string.Join('|', graph.InheritedGroups("app"))).IsEqualTo("readers");

        // readers itself is a member of ops, so ops still walks the tree with admins under it.
        var readersGroups = graph.MemberOf("readers");
        await Assert.That(readersGroups.Single().Role).IsEqualTo("ops");
        await Assert.That(readersGroups.Single().Inherits).IsFalse();
        await Assert.That(readersGroups.Single().Children.Single().Role).IsEqualTo("admins");
    }

    [Test]
    public async Task MembershipCycleTerminates()
    {
        // Postgres refuses to create this, but the snapshot is read across
        // statements and a malformed input must not hang the UI.
        var graph = RoleGraph.Build(
            [Role("a"), Role("b"), Role("c")],
            [Edge("a", "b"), Edge("b", "c"), Edge("c", "a")]);

        await Assert.That(string.Join('|', graph.InheritedGroups("a"))).IsEqualTo("b|c");

        var memberOf = graph.MemberOf("a");
        await Assert.That(memberOf.Single().Role).IsEqualTo("b");
        await Assert.That(memberOf.Single().Children.Single().Role).IsEqualTo("c");

        // c's only group is a, which is the root of this walk — the edge that
        // closes the loop is dropped rather than followed.
        await Assert.That(memberOf.Single().Children.Single().Children).IsEmpty();

        var members = graph.Members("a");
        await Assert.That(members.Single().Role).IsEqualTo("c");
        await Assert.That(members.Single().Children.Single().Role).IsEqualTo("b");
        await Assert.That(members.Single().Children.Single().Children).IsEmpty();
    }

    [Test]
    public async Task SelfMembershipIsIgnored()
    {
        var graph = RoleGraph.Build([Role("a")], [Edge("a", "a")]);

        await Assert.That(graph.MemberOf("a")).IsEmpty();
        await Assert.That(graph.Members("a")).IsEmpty();
        await Assert.That(graph.InheritedGroups("a")).IsEmpty();
    }

    [Test]
    public async Task UnknownRolesAnswerEmptyRatherThanThrowing()
    {
        var graph = RoleGraph.Build([Role("app")], [Edge("app", "readers")]);

        await Assert.That(graph.InheritedGroups("nobody")).IsEmpty();
        await Assert.That(graph.MemberOf("nobody")).IsEmpty();
        await Assert.That(graph.Members("nobody")).IsEmpty();
        await Assert.That(graph.Find("nobody")).IsNull();
        await Assert.That(graph.IsSuperuser("nobody")).IsFalse();
    }

    [Test]
    public async Task GroupNamedOnlyByAnEdgeStillWalks()
    {
        // The membership names readers, but the role list never did (a filtered
        // snapshot). The edge is still real, so the tree shows it and Find says so.
        var graph = RoleGraph.Build([Role("app")], [Edge("app", "readers")]);

        await Assert.That(graph.MemberOf("app").Single().Role).IsEqualTo("readers");
        await Assert.That(string.Join('|', graph.InheritedGroups("app"))).IsEqualTo("readers");
        await Assert.That(graph.Find("readers")).IsNull();
    }

    [Test]
    public async Task SuperuserIsLookedUpByName()
    {
        var graph = RoleGraph.Build([Role("postgres", superuser: true), Role("app")], []);

        await Assert.That(graph.IsSuperuser("postgres")).IsTrue();
        await Assert.That(graph.IsSuperuser("app")).IsFalse();
        await Assert.That(graph.Find("app")!.Name).IsEqualTo("app");
    }

    [Test]
    public async Task SiblingsAndRolesAreAlphabetical()
    {
        var graph = RoleGraph.Build(
            [Role("zulu"), Role("alpha"), Role("mike"), Role("staff")],
            [Edge("staff", "zulu"), Edge("staff", "alpha"), Edge("staff", "mike"),
             Edge("zulu", "staff"), Edge("alpha", "staff"), Edge("mike", "staff")]);

        await Assert.That(string.Join('|', graph.Roles.Select(r => r.Name)))
            .IsEqualTo("alpha|mike|staff|zulu");

        // Upward siblings sort by group name, downward siblings by member name.
        await Assert.That(string.Join('|', graph.MemberOf("staff").Select(n => n.Role)))
            .IsEqualTo("alpha|mike|zulu");
        await Assert.That(string.Join('|', graph.Members("staff").Select(n => n.Role)))
            .IsEqualTo("alpha|mike|zulu");
    }

    [Test]
    public async Task DuplicateEdgesCollapse()
    {
        var graph = RoleGraph.Build(
            [Role("app"), Role("readers")],
            [Edge("app", "readers"), Edge("app", "readers")]);

        await Assert.That(graph.MemberOf("app")).Count().IsEqualTo(1);
        await Assert.That(graph.InheritedGroups("app")).Count().IsEqualTo(1);
    }
}
