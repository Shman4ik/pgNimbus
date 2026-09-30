namespace PgNimbus.Core.Security;

/// <summary>
/// The role membership graph, indexed both ways: the roles a role belongs to and
/// the roles that belong to it. Pure logic (Core stays Avalonia- and Npgsql-free
/// here), unit-tested — the App binds a <c>TreeView</c> to the nodes and the
/// effective-privilege resolver asks it the two <see cref="IRoleMembershipLookup"/>
/// questions. Same builder shape as <c>Monitoring.BlockingTree</c>, for the same
/// reason: the interesting behaviour is graph walking, which deserves tests that
/// need no server.
///
/// The behaviour that makes this more than a dictionary is <c>NOINHERIT</c>. A
/// membership whose <see cref="RoleMembership.InheritOption"/> is false gives the
/// member the group's privileges only after an explicit <c>SET ROLE</c>, so those
/// groups are shown in the membership tree (the relationship is real and the user
/// needs to see it) but never reported by <see cref="InheritedGroups"/> — claiming
/// a privilege the server will refuse is worse than showing none.
/// </summary>
public sealed class RoleGraph : IRoleMembershipLookup
{
    private readonly Dictionary<string, RoleAttributes> _byName;

    /// <summary>member name → the memberships that walk upward, sorted by group name.</summary>
    private readonly Dictionary<string, List<RoleMembership>> _up;

    /// <summary>group name → the memberships that walk downward, sorted by member name.</summary>
    private readonly Dictionary<string, List<RoleMembership>> _down;

    private RoleGraph(
        Dictionary<string, RoleAttributes> byName,
        Dictionary<string, List<RoleMembership>> up,
        Dictionary<string, List<RoleMembership>> down,
        IReadOnlyList<RoleAttributes> roles)
    {
        _byName = byName;
        _up = up;
        _down = down;
        Roles = roles;
    }

    /// <summary>Every role the graph was built from, alphabetically.</summary>
    public IReadOnlyList<RoleAttributes> Roles { get; }

    /// <summary>
    /// Indexes a role snapshot. Role names are compared ordinally: Postgres stores
    /// them case-sensitively, and folding <c>Admin</c> into <c>admin</c> here would
    /// merge two genuinely different roles.
    /// </summary>
    public static RoleGraph Build(IEnumerable<RoleAttributes> roles, IEnumerable<RoleMembership> memberships)
    {
        var byName = new Dictionary<string, RoleAttributes>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            // Later duplicates (shouldn't happen from the catalog) just overwrite.
            byName[role.Name] = role;
        }

        var up = new Dictionary<string, List<RoleMembership>>(StringComparer.Ordinal);
        var down = new Dictionary<string, List<RoleMembership>>(StringComparer.Ordinal);
        var seenEdges = new HashSet<(string Member, string Group)>();

        foreach (var edge in memberships)
        {
            // A role that is its own member is not a thing Postgres will create, and
            // it would only ever be filtered back out by the cycle guard below.
            if (string.Equals(edge.Member, edge.Group, StringComparison.Ordinal))
            {
                continue;
            }

            if (!seenEdges.Add((edge.Member, edge.Group)))
            {
                continue;
            }

            Index(up, edge.Member, edge);
            Index(down, edge.Group, edge);
        }

        // Siblings render alphabetically: the tree is drawn straight from these
        // lists and a screenshot baseline compares the result, so dictionary
        // enumeration order is not good enough.
        foreach (var edges in up.Values)
        {
            edges.Sort(static (a, b) => string.CompareOrdinal(a.Group, b.Group));
        }

        foreach (var edges in down.Values)
        {
            edges.Sort(static (a, b) => string.CompareOrdinal(a.Member, b.Member));
        }

        var ordered = byName.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        return new RoleGraph(byName, up, down, ordered);

        static void Index(Dictionary<string, List<RoleMembership>> index, string key, RoleMembership edge)
        {
            if (!index.TryGetValue(key, out var edges))
            {
                edges = [];
                index[key] = edges;
            }

            edges.Add(edge);
        }
    }

    /// <summary>
    /// The roles <paramref name="role"/> belongs to, walking upward — a group that
    /// is itself a member of another group nests. Empty for an unknown role.
    /// </summary>
    public IReadOnlyList<RoleTreeNode> MemberOf(string role) =>
        Walk(role, _up, static edge => edge.Group, Path(role));

    /// <summary>
    /// The roles that belong to <paramref name="role"/>, walking downward. Empty
    /// for an unknown role.
    /// </summary>
    public IReadOnlyList<RoleTreeNode> Members(string role) =>
        Walk(role, _down, static edge => edge.Member, Path(role));

    /// <inheritdoc />
    public IReadOnlyList<string> InheritedGroups(string role)
    {
        // Breadth-first so the result is nearest-first: a privilege the role picks
        // up from its own group is a better explanation than the same privilege
        // three groups further up. A group reachable by two paths (the diamond)
        // is reported once, at the shortest distance.
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { role };
        var frontier = new List<string> { role };

        while (frontier.Count > 0)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                if (!_up.TryGetValue(current, out var edges))
                {
                    continue;
                }

                foreach (var edge in edges)
                {
                    // NOINHERIT: the membership exists but its privileges are dormant
                    // until SET ROLE, so the walk stops here rather than reporting
                    // access the server would refuse.
                    if (!edge.InheritOption)
                    {
                        continue;
                    }

                    // Doubles as the cycle guard — a snapshot read across statements
                    // can contain a loop no live catalog would allow.
                    if (seen.Add(edge.Group))
                    {
                        next.Add(edge.Group);
                    }
                }
            }

            next.Sort(StringComparer.Ordinal);
            result.AddRange(next);
            frontier = next;
        }

        return result;
    }

    /// <inheritdoc />
    public bool IsSuperuser(string role) => _byName.TryGetValue(role, out var attributes) && attributes.IsSuperuser;

    /// <summary>The role's attributes, or null when the snapshot never mentioned it.</summary>
    public RoleAttributes? Find(string role) => _byName.GetValueOrDefault(role);

    private static HashSet<string> Path(string role) => new(StringComparer.Ordinal) { role };

    private static IReadOnlyList<RoleTreeNode> Walk(
        string role,
        Dictionary<string, List<RoleMembership>> index,
        Func<RoleMembership, string> other,
        HashSet<string> path)
    {
        if (!index.TryGetValue(role, out var edges))
        {
            return [];
        }

        var nodes = new List<RoleTreeNode>();
        foreach (var edge in edges)
        {
            var next = other(edge);

            // An edge back onto the current path closes a cycle; following it would
            // recurse forever and hang the UI.
            if (path.Contains(next))
            {
                continue;
            }

            var childPath = new HashSet<string>(path, StringComparer.Ordinal) { next };
            nodes.Add(new RoleTreeNode(next, edge.InheritOption, Walk(next, index, other, childPath)));
        }

        return nodes;
    }
}
