namespace PgNimbus.Core.Security;

/// <summary>
/// Turns a stored ACL into the answer a person actually asked for: can this role
/// do this to this object, and which grant explains it.
///
/// This is the differentiator. pgAdmin, DBeaver, DataGrip and TablePlus all
/// render the ACL as it sits in the catalog, which silently omits every
/// privilege reached through role membership, through ownership, through PUBLIC,
/// or through a NULL ACL column. A permission that works is invisible; a
/// permission that is missing looks present. This walks the same ground the
/// server does and names the source.
///
/// Pure logic — a read-only sibling of <c>PlanAnalyzer</c>, <c>BlockingTree</c>
/// and <c>JsonTree</c>. No Npgsql, no Avalonia, fully unit-tested: the role
/// graph arrives through <see cref="IRoleMembershipLookup"/> and the server's
/// own verdict through an optional answers dictionary, so there is nothing here
/// that needs a database to exercise.
/// </summary>
public static class EffectivePrivilegeResolver
{
    /// <summary>
    /// Resolves every <paramref name="roles"/> × <paramref name="privileges"/>
    /// pair against <paramref name="acl"/>, in that nesting order.
    ///
    /// The resolution order below <em>is</em> the semantics, so it is spelled
    /// out rather than left to the reader of the switch. First match wins:
    ///
    /// <list type="number">
    /// <item>Superuser — bypasses permission checks entirely, so nothing else matters.</item>
    /// <item>Owner — ownership carries every privilege implicitly, and outranks
    /// any explicit grant that happens to name the owner as well.</item>
    /// <item>Direct — an ACL entry naming the role itself.</item>
    /// <item>Inherited — an ACL entry naming a group the role inherits from,
    /// attributed to the <em>nearest</em> such group.</item>
    /// <item>PUBLIC — an entry granted to everyone, including the built-in
    /// defaults that apply when the ACL column is NULL.</item>
    /// <item>Otherwise not granted.</item>
    /// </list>
    ///
    /// When <paramref name="serverAnswers"/> is supplied the server wins every
    /// disagreement; see the reconciliation block below.
    /// </summary>
    public static IReadOnlyList<EffectivePrivilege> Resolve(
        ObjectAcl acl,
        IReadOnlyList<string> roles,
        IReadOnlyList<PrivilegeKind> privileges,
        IRoleMembershipLookup lookup,
        IReadOnlyDictionary<(string Role, PrivilegeKind Privilege), bool>? serverAnswers = null)
    {
        var results = new List<EffectivePrivilege>(roles.Count * privileges.Count);

        foreach (var role in roles)
        {
            var isSuperuser = lookup.IsSuperuser(role);
            var isOwner = string.Equals(role, acl.Owner, StringComparison.Ordinal);

            // Nearest-first, per IRoleMembershipLookup's contract — the walk stops
            // at a NOINHERIT edge, so anything reachable only through SET ROLE is
            // already excluded and must not be reported as effective.
            IReadOnlyList<string> groups = isSuperuser || isOwner ? [] : lookup.InheritedGroups(role);

            foreach (var privilege in privileges)
            {
                var resolved = ResolveOne(acl, role, privilege, isSuperuser, isOwner, groups);

                if (serverAnswers is not null
                    && serverAnswers.TryGetValue((role, privilege), out var serverSaysGranted))
                {
                    resolved = Reconcile(resolved, serverSaysGranted);
                }

                results.Add(resolved);
            }
        }

        return results;
    }

    private static EffectivePrivilege ResolveOne(
        ObjectAcl acl,
        string role,
        PrivilegeKind privilege,
        bool isSuperuser,
        bool isOwner,
        IReadOnlyList<string> groups)
    {
        if (isSuperuser)
        {
            return new EffectivePrivilege(role, privilege, true, PrivilegeSource.Superuser);
        }

        if (isOwner)
        {
            return new EffectivePrivilege(role, privilege, true, PrivilegeSource.Owner);
        }

        if (FindEntry(acl, privilege, role) is { } direct)
        {
            return new EffectivePrivilege(
                role, privilege, true, PrivilegeSource.Direct,
                GrantedBy: direct.Grantor,
                WithGrantOption: direct.WithGrantOption);
        }

        // Nearest group first, so the attribution names the role the user is
        // most likely to recognise as the one they should edit. Scanning the ACL
        // first instead would attribute to whichever grant happens to sit
        // earliest in the catalog, which is arbitrary.
        foreach (var group in groups)
        {
            if (FindEntry(acl, privilege, group) is { } inherited)
            {
                return new EffectivePrivilege(
                    role, privilege, true, PrivilegeSource.Inherited,
                    Via: group,
                    GrantedBy: inherited.Grantor,
                    WithGrantOption: inherited.WithGrantOption);
            }
        }

        if (FindEntry(acl, privilege, null) is { } @public)
        {
            return new EffectivePrivilege(
                role, privilege, true, PrivilegeSource.Public,
                GrantedBy: @public.Grantor,
                WithGrantOption: @public.WithGrantOption);
        }

        // A NULL ACL column has no entries at all, but Postgres's built-in
        // defaults still apply, and some of them are grants to PUBLIC. Reporting
        // those as "not granted" is the mistake that makes a permissions grid
        // teach the wrong thing (see ObjectAcl.IsDefaultAcl).
        if (acl.IsDefaultAcl && GrantedToPublicByDefault(acl.Object.Kind, privilege))
        {
            return new EffectivePrivilege(role, privilege, true, PrivilegeSource.Public);
        }

        return new EffectivePrivilege(role, privilege, false, PrivilegeSource.None);
    }

    /// <summary>
    /// The server's verdict always wins — it ran the same check the executor
    /// will run, expanding inheritance, ownership and superuser server-side.
    /// Where we agree, our source survives as the explanation. Where we do not,
    /// the disagreement is reported honestly rather than papered over:
    /// a grant we cannot see becomes <see cref="PrivilegeSource.Unknown"/>, and
    /// a grant we imagined (a NOINHERIT edge, a revoke that landed after this
    /// snapshot was taken) collapses back to <see cref="PrivilegeSource.None"/>.
    /// </summary>
    private static EffectivePrivilege Reconcile(EffectivePrivilege resolved, bool serverSaysGranted) =>
        (serverSaysGranted, resolved.Granted) switch
        {
            (true, true) => resolved,
            (true, false) => resolved with { Granted = true, Source = PrivilegeSource.Unknown },
            (false, true) => new EffectivePrivilege(resolved.Role, resolved.Privilege, false, PrivilegeSource.None),
            (false, false) => resolved,
        };

    /// <summary>
    /// The one ACL entry granting <paramref name="privilege"/> to
    /// <paramref name="grantee"/> (null meaning PUBLIC), or null. When several
    /// grantors granted the same thing, one carrying WITH GRANT OPTION wins so
    /// the reported capability is not understated; otherwise the first entry
    /// wins, which keeps the result deterministic.
    /// </summary>
    private static AclEntry? FindEntry(ObjectAcl acl, PrivilegeKind privilege, string? grantee)
    {
        AclEntry? first = null;

        foreach (var entry in acl.Entries)
        {
            if (entry.Privilege != privilege
                || !string.Equals(entry.Grantee, grantee, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.WithGrantOption)
            {
                return entry;
            }

            first ??= entry;
        }

        return first;
    }

    /// <summary>
    /// What PUBLIC holds on an object nobody has ever run a GRANT or REVOKE on.
    /// Postgres grants EXECUTE on functions, USAGE on types, and CONNECT plus
    /// TEMPORARY on databases by default; tables, sequences and schemas start
    /// with nothing for PUBLIC (the <c>public</c> schema's historical CREATE
    /// grant is a real ACL entry, not a default, and stopped existing in PG15).
    /// </summary>
    private static bool GrantedToPublicByDefault(SecurableKind kind, PrivilegeKind privilege) =>
        (kind, privilege) switch
        {
            (SecurableKind.Function, PrivilegeKind.Execute) => true,
            (SecurableKind.Type, PrivilegeKind.Usage) => true,
            (SecurableKind.Database, PrivilegeKind.Connect) => true,
            (SecurableKind.Database, PrivilegeKind.Temporary) => true,
            _ => false,
        };

    // ------------------------------------------------------------- the sentence

    /// <summary>
    /// The plain-English answer, for people who would rather not decode
    /// <c>{readers=arwdDxt/postgres}</c>: what the role can do and what explains
    /// it, then what it cannot do, then the schema-USAGE trap if it applies.
    ///
    /// Deterministic by construction — privileges are listed in the order
    /// <paramref name="effective"/> gives them, and the source phrasing comes
    /// from <see cref="EffectivePrivilege.Explanation"/> rather than being
    /// re-derived here, so the sentence and the "why?" column can never drift
    /// apart.
    /// </summary>
    /// <param name="hasSchemaUsage">
    /// From <c>PrivilegeService.HasSchemaUsageAsync</c>. When false and the
    /// object lives in a schema, every grant above is inert — the single most
    /// common reason a role with all the right privileges still gets
    /// <c>permission denied</c>, so it gets its own clause.
    /// </param>
    public static string ExplainSentence(
        string role,
        SecurableRef obj,
        IReadOnlyList<EffectivePrivilege> effective,
        bool hasSchemaUsage)
    {
        var mine = effective.Where(e => string.Equals(e.Role, role, StringComparison.Ordinal)).ToList();
        var granted = mine.Where(e => e.Granted).ToList();
        var denied = mine.Where(e => !e.Granted).ToList();

        var sentence = granted.Count == 0
            ? $"{role} has no privileges on {obj.Display}."
            : $"{role} can {Join(granted, "and")} {obj.Display} — {granted[0].Explanation}.";

        if (granted.Count > 0 && denied.Count > 0)
        {
            sentence += $" It cannot {Join(denied, "or")}.";
        }

        if (!hasSchemaUsage && obj.Schema is { Length: > 0 } schema)
        {
            sentence += $" {role} also lacks USAGE on schema {schema}, which blocks access to everything in it.";
        }

        return sentence;
    }

    /// <summary>"SELECT", "SELECT and INSERT", "SELECT, INSERT and UPDATE".</summary>
    private static string Join(IReadOnlyList<EffectivePrivilege> items, string conjunction)
    {
        var names = items.Select(e => Privileges.Sql(e.Privilege)).ToList();

        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} {conjunction} {names[^1]}",
        };
    }
}
