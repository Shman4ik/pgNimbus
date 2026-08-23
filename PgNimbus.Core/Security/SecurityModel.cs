using PgNimbus.Core.Query;

namespace PgNimbus.Core.Security;

/// <summary>
/// Every privilege Postgres can grant, as the <c>aclexplode()</c>
/// <c>privilege_type</c> text names them. Kept as one flat enum rather than one
/// per object class because the permissions grid is a single matrix — which
/// members actually apply to a given object is <see cref="Privileges.For"/>'s
/// job, not the type system's.
/// </summary>
public enum PrivilegeKind
{
    Select,
    Insert,
    Update,
    Delete,
    Truncate,
    References,
    Trigger,

    /// <summary>PG17+: VACUUM/ANALYZE/REINDEX/CLUSTER without ownership.</summary>
    Maintain,

    Usage,
    Create,
    Connect,
    Temporary,
    Execute,

    /// <summary>PG15+: SET on a configuration parameter.</summary>
    Set,

    /// <summary>PG15+: ALTER SYSTEM on a configuration parameter.</summary>
    AlterSystem,
}

/// <summary>
/// The class of thing a privilege is granted on. Views, materialized views,
/// partitioned tables and foreign tables are all <see cref="Table"/> — Postgres
/// grants on them with the same <c>TABLE</c> keyword and the same privilege
/// set, so splitting them here would buy nothing but a wider switch.
/// </summary>
public enum SecurableKind
{
    Table,
    Sequence,
    Schema,
    Database,
    Function,
    Type,
}

/// <summary>
/// How a role came to hold (or not hold) a privilege on an object. This is the
/// column the incumbents don't have: pgAdmin, DBeaver and DataGrip all render
/// the stored ACL, which silently omits everything reached through role
/// membership, ownership or PUBLIC.
/// </summary>
public enum PrivilegeSource
{
    /// <summary>The role does not have the privilege.</summary>
    None,

    /// <summary>A GRANT names this role directly.</summary>
    Direct,

    /// <summary>A GRANT names a role this one inherits from; see <see cref="EffectivePrivilege.Via"/>.</summary>
    Inherited,

    /// <summary>Granted to PUBLIC, so every role has it.</summary>
    Public,

    /// <summary>The role owns the object, which carries all privileges implicitly.</summary>
    Owner,

    /// <summary>The role is a superuser and bypasses permission checks entirely.</summary>
    Superuser,

    /// <summary>
    /// The server says yes but nothing in the catalog we read explains why —
    /// e.g. a grant through a role we could not see, or a privilege reached via
    /// a chain the resolver declined to walk. Rendered honestly rather than
    /// guessed at.
    /// </summary>
    Unknown,
}

/// <summary>
/// Which privileges apply to which object class, and the text names Postgres
/// uses for them in <c>GRANT</c> and <c>has_*_privilege()</c>. Pure lookup —
/// no catalog access.
/// </summary>
public static class Privileges
{
    /// <summary>The SQL keyword: <c>PrivilegeKind.AlterSystem</c> → <c>"ALTER SYSTEM"</c>.</summary>
    public static string Sql(PrivilegeKind privilege) => privilege switch
    {
        PrivilegeKind.Select => "SELECT",
        PrivilegeKind.Insert => "INSERT",
        PrivilegeKind.Update => "UPDATE",
        PrivilegeKind.Delete => "DELETE",
        PrivilegeKind.Truncate => "TRUNCATE",
        PrivilegeKind.References => "REFERENCES",
        PrivilegeKind.Trigger => "TRIGGER",
        PrivilegeKind.Maintain => "MAINTAIN",
        PrivilegeKind.Usage => "USAGE",
        PrivilegeKind.Create => "CREATE",
        PrivilegeKind.Connect => "CONNECT",
        PrivilegeKind.Temporary => "TEMPORARY",
        PrivilegeKind.Execute => "EXECUTE",
        PrivilegeKind.Set => "SET",
        PrivilegeKind.AlterSystem => "ALTER SYSTEM",
        _ => throw new ArgumentOutOfRangeException(nameof(privilege), privilege, null),
    };

    /// <summary>
    /// Parses the <c>privilege_type</c> text <c>aclexplode()</c> and
    /// <c>information_schema</c> emit. Returns null for anything we don't model
    /// (a future server's new privilege) so an unknown row is skipped rather
    /// than throwing the whole grid away.
    /// </summary>
    public static PrivilegeKind? Parse(string privilegeType) => privilegeType.ToUpperInvariant() switch
    {
        "SELECT" => PrivilegeKind.Select,
        "INSERT" => PrivilegeKind.Insert,
        "UPDATE" => PrivilegeKind.Update,
        "DELETE" => PrivilegeKind.Delete,
        "TRUNCATE" => PrivilegeKind.Truncate,
        "REFERENCES" => PrivilegeKind.References,
        "TRIGGER" => PrivilegeKind.Trigger,
        "MAINTAIN" => PrivilegeKind.Maintain,
        "USAGE" => PrivilegeKind.Usage,
        "CREATE" => PrivilegeKind.Create,
        "CONNECT" => PrivilegeKind.Connect,
        "TEMPORARY" or "TEMP" => PrivilegeKind.Temporary,
        "EXECUTE" => PrivilegeKind.Execute,
        "SET" => PrivilegeKind.Set,
        "ALTER SYSTEM" => PrivilegeKind.AlterSystem,
        _ => null,
    };

    /// <summary>
    /// The privileges that can be granted on <paramref name="kind"/>, in the
    /// order the permissions matrix shows its columns.
    /// <paramref name="serverVersion"/> gates the ones that don't exist on
    /// older servers (MAINTAIN is PG17+); pass null to include everything.
    /// </summary>
    public static IReadOnlyList<PrivilegeKind> For(SecurableKind kind, Version? serverVersion = null) => kind switch
    {
        SecurableKind.Table => PgFeatures.SupportsMaintain(serverVersion)
            ?
            [
                PrivilegeKind.Select, PrivilegeKind.Insert, PrivilegeKind.Update, PrivilegeKind.Delete,
                PrivilegeKind.Truncate, PrivilegeKind.References, PrivilegeKind.Trigger, PrivilegeKind.Maintain,
            ]
            :
            [
                PrivilegeKind.Select, PrivilegeKind.Insert, PrivilegeKind.Update, PrivilegeKind.Delete,
                PrivilegeKind.Truncate, PrivilegeKind.References, PrivilegeKind.Trigger,
            ],
        SecurableKind.Sequence => [PrivilegeKind.Usage, PrivilegeKind.Select, PrivilegeKind.Update],
        SecurableKind.Schema => [PrivilegeKind.Usage, PrivilegeKind.Create],
        SecurableKind.Database => [PrivilegeKind.Connect, PrivilegeKind.Create, PrivilegeKind.Temporary],
        SecurableKind.Function => [PrivilegeKind.Execute],
        SecurableKind.Type => [PrivilegeKind.Usage],
        _ => [],
    };

    /// <summary>The privileges grantable on a single column (a strict subset of the table's).</summary>
    public static IReadOnlyList<PrivilegeKind> ForColumn() =>
        [PrivilegeKind.Select, PrivilegeKind.Insert, PrivilegeKind.Update, PrivilegeKind.References];

    /// <summary>
    /// The <c>has_*_privilege</c> function that answers "can this role do this
    /// to this object?" — the authoritative check, because unlike a raw ACL read
    /// it expands role inheritance, ownership and superuser for us.
    /// </summary>
    public static string HasPrivilegeFunction(SecurableKind kind) => kind switch
    {
        SecurableKind.Table => "has_table_privilege",
        SecurableKind.Sequence => "has_sequence_privilege",
        SecurableKind.Schema => "has_schema_privilege",
        SecurableKind.Database => "has_database_privilege",
        SecurableKind.Function => "has_function_privilege",
        SecurableKind.Type => "has_type_privilege",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The catalog table an object of this class lives in, for the ACL read.</summary>
    public static string CatalogTable(SecurableKind kind) => kind switch
    {
        SecurableKind.Table or SecurableKind.Sequence => "pg_catalog.pg_class",
        SecurableKind.Schema => "pg_catalog.pg_namespace",
        SecurableKind.Database => "pg_catalog.pg_database",
        SecurableKind.Function => "pg_catalog.pg_proc",
        SecurableKind.Type => "pg_catalog.pg_type",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// Server-version gates for the catalog columns and privileges that don't exist
/// everywhere. A null version means "assume modern" — the screenshot/test
/// fixtures have no server to ask.
/// </summary>
public static class PgFeatures
{
    /// <summary>PG16 split <c>pg_auth_members.admin_option</c> into admin/inherit/set options.</summary>
    public static bool SupportsRoleMemberOptions(Version? v) => v is null || v.Major >= 16;

    /// <summary>The MAINTAIN privilege arrived in PG17.</summary>
    public static bool SupportsMaintain(Version? v) => v is null || v.Major >= 17;

    /// <summary>The <c>pg_read_all_data</c> / <c>pg_write_all_data</c> predefined roles arrived in PG14.</summary>
    public static bool SupportsPredefinedDataRoles(Version? v) => v is null || v.Major >= 14;
}

/// <summary>
/// One object a privilege can be held on, carrying the OID because every
/// <c>has_*_privilege()</c> check takes one — resolving by name would re-do the
/// search-path dance the catalog already did for us.
/// </summary>
/// <param name="Arguments">
/// A function's argument list (<c>integer, text</c>) — part of its identity, so
/// two overloads are two securables. Null for everything else.
/// </param>
public sealed record SecurableRef(
    SecurableKind Kind,
    uint Oid,
    string? Schema,
    string Name,
    string? Arguments = null)
{
    /// <summary>How the object is named in the UI: <c>sales.orders</c>, <c>public.f(integer)</c>.</summary>
    public string Display =>
        (Schema is null ? Name : $"{Schema}.{Name}")
        + (Arguments is null ? "" : $"({Arguments})");

    /// <summary>The quoted, schema-qualified name — safe to interpolate into SQL.</summary>
    public string QuotedName =>
        Schema is null
            ? SqlIdentifier.QuoteIfNeeded(Name)
            : $"{SqlIdentifier.QuoteIfNeeded(Schema)}.{SqlIdentifier.QuoteIfNeeded(Name)}";

    /// <summary>
    /// The <c>ON …</c> clause of a GRANT/REVOKE for this object, keyword
    /// included: <c>TABLE "sales"."orders"</c>, <c>SCHEMA sales</c>,
    /// <c>FUNCTION public.f(integer)</c>.
    /// </summary>
    public string GrantTarget => Kind switch
    {
        SecurableKind.Table => $"TABLE {QuotedName}",
        SecurableKind.Sequence => $"SEQUENCE {QuotedName}",
        SecurableKind.Schema => $"SCHEMA {QuotedName}",
        SecurableKind.Database => $"DATABASE {QuotedName}",
        SecurableKind.Function => $"FUNCTION {QuotedName}({Arguments ?? ""})",
        SecurableKind.Type => $"TYPE {QuotedName}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };
}

/// <summary>
/// A role as <c>pg_roles</c> describes it. Deliberately not <c>pg_authid</c> —
/// that needs superuser and the only thing it adds is the password hash, which
/// this app has no business reading.
/// </summary>
/// <param name="Settings">
/// The <c>rolconfig</c> entries (<c>ALTER ROLE … SET x = y</c>), verbatim.
/// </param>
public sealed record RoleAttributes(
    uint Oid,
    string Name,
    bool CanLogin,
    bool IsSuperuser,
    bool Inherit,
    bool CanCreateRole,
    bool CanCreateDb,
    bool CanReplicate,
    bool BypassRls,
    int ConnectionLimit,
    DateTimeOffset? ValidUntil,
    IReadOnlyList<string> Settings,
    string? Comment)
{
    /// <summary>A built-in <c>pg_*</c> role — listed, never offered for edit or drop.</summary>
    public bool IsPredefined => Name.StartsWith("pg_", StringComparison.Ordinal);

    /// <summary>True when the role can be connected as; the "user vs group" distinction users actually mean.</summary>
    public bool IsUser => CanLogin;

    /// <summary>Already expired — a login role nobody can log in as any more.</summary>
    public bool IsExpired => ValidUntil is { } until && until <= DateTimeOffset.UtcNow;
}

/// <summary>
/// One edge of the role membership graph: <paramref name="Member"/> is a member
/// of <paramref name="Group"/>.
/// </summary>
/// <param name="InheritOption">
/// Whether the member automatically holds the group's privileges. PG16 made this
/// per-grant; on older servers it is the member's own <c>rolinherit</c>. False
/// means the privileges exist but only after an explicit <c>SET ROLE</c> — the
/// distinction that makes a permission look missing when it is merely dormant.
/// </param>
public sealed record RoleMembership(
    string Member,
    string Group,
    bool AdminOption,
    bool InheritOption,
    bool SetOption,
    string? Grantor);

/// <summary>
/// A node of the rendered membership tree. Built in both directions — the roles
/// a role belongs to, and the roles that belong to it.
/// </summary>
/// <param name="Inherits">
/// The <see cref="RoleMembership.InheritOption"/> of the edge that reached this
/// node; the root's is always true.
/// </param>
public sealed record RoleTreeNode(string Role, bool Inherits, IReadOnlyList<RoleTreeNode> Children);

/// <summary>
/// Answers the two questions the effective-privilege resolver needs about the
/// role graph, so the resolver can stay pure and be tested without a server.
/// Implemented by <c>RoleGraph</c>.
/// </summary>
public interface IRoleMembershipLookup
{
    /// <summary>
    /// Every role whose privileges <paramref name="role"/> holds automatically,
    /// transitively, nearest first, excluding <paramref name="role"/> itself.
    /// A <c>NOINHERIT</c> edge stops the walk — those privileges need
    /// <c>SET ROLE</c> and must not be reported as effective.
    /// </summary>
    IReadOnlyList<string> InheritedGroups(string role);

    /// <summary>True when the role bypasses permission checks entirely.</summary>
    bool IsSuperuser(string role);
}

/// <summary>
/// One <c>GRANT</c> recorded in an object's ACL, as <c>aclexplode()</c> returns
/// it. <paramref name="Grantee"/> is null for <c>PUBLIC</c>.
/// </summary>
public sealed record AclEntry(
    string? Grantee,
    string? Grantor,
    PrivilegeKind Privilege,
    bool WithGrantOption)
{
    public bool IsPublic => Grantee is null;

    /// <summary>What the UI shows in the grantee column.</summary>
    public string GranteeLabel => Grantee ?? "PUBLIC";
}

/// <summary>
/// An object's stored access control list.
/// </summary>
/// <param name="IsDefaultAcl">
/// The catalog column was NULL, which in Postgres means "nobody has touched the
/// privileges: the owner has everything and the built-in defaults apply" — not
/// "no privileges". Rendering that state as an empty grid is the single most
/// common way a permissions UI teaches the wrong thing, so it is modelled
/// explicitly here instead of collapsing to an empty list.
/// </param>
public sealed record ObjectAcl(
    SecurableRef Object,
    string Owner,
    bool IsDefaultAcl,
    IReadOnlyList<AclEntry> Entries);

/// <summary>Per-column grants (<c>pg_attribute.attacl</c>) on one table.</summary>
public sealed record ColumnAcl(string Column, bool IsDefaultAcl, IReadOnlyList<AclEntry> Entries);

/// <summary>
/// One <c>pg_default_acl</c> row: what a future object created by
/// <paramref name="OwnerRole"/> in <paramref name="Schema"/> will be granted.
/// </summary>
/// <param name="Schema">
/// Null for the database-wide default. Per-schema defaults *add* to the
/// database-wide ones and cannot subtract from them — a trap worth surfacing in
/// the panel rather than a doc.
/// </param>
public sealed record DefaultPrivilege(
    string OwnerRole,
    string? Schema,
    SecurableKind AppliesTo,
    IReadOnlyList<AclEntry> Entries);

/// <summary>One row-level security policy, as <c>pg_policies</c> describes it.</summary>
/// <param name="Roles">The roles it applies to; a single "public" entry means everyone.</param>
/// <param name="Command">ALL / SELECT / INSERT / UPDATE / DELETE.</param>
public sealed record RlsPolicyInfo(
    string Schema,
    string Table,
    string Name,
    bool Permissive,
    IReadOnlyList<string> Roles,
    string Command,
    string? Using,
    string? WithCheck);

/// <summary>
/// A table's RLS state plus its policies. <paramref name="BypassedByCurrentRole"/>
/// is the footgun that makes RLS "work for me but not for the app": the table
/// owner and any BYPASSRLS role never see the policies apply, unless the table
/// is FORCE ROW LEVEL SECURITY.
/// </summary>
public sealed record RlsTableState(
    string Schema,
    string Table,
    bool RowSecurityEnabled,
    bool ForceRowSecurity,
    bool BypassedByCurrentRole,
    IReadOnlyList<RlsPolicyInfo> Policies)
{
    /// <summary>Policies exist but RLS was never switched on, so none of them do anything.</summary>
    public bool HasInertPolicies => !RowSecurityEnabled && Policies.Count > 0;
}

/// <summary>
/// The resolved answer for one role × one privilege on one object: whether the
/// server will allow it, and which grant explains that.
/// </summary>
/// <param name="Via">The intermediate role for <see cref="PrivilegeSource.Inherited"/>; null otherwise.</param>
public sealed record EffectivePrivilege(
    string Role,
    PrivilegeKind Privilege,
    bool Granted,
    PrivilegeSource Source,
    string? Via = null,
    string? GrantedBy = null,
    bool WithGrantOption = false)
{
    /// <summary>The one-line explanation the "why?" column and the access sentence share.</summary>
    public string Explanation => Source switch
    {
        PrivilegeSource.None => "not granted",
        PrivilegeSource.Direct => GrantedBy is { Length: > 0 } g ? $"granted directly by {g}" : "granted directly",
        PrivilegeSource.Inherited => $"inherited from {Via}",
        PrivilegeSource.Public => "granted to PUBLIC",
        PrivilegeSource.Owner => "owns the object",
        PrivilegeSource.Superuser => "superuser",
        PrivilegeSource.Unknown => "granted, source not visible from here",
        _ => "",
    };
}

/// <summary>
/// Something that stops a role being dropped — an object it owns, or a privilege
/// it has been granted elsewhere. Listing these up front is what turns
/// Postgres's opaque 2BP01 into an actionable dialog.
/// </summary>
public sealed record RoleDependency(string Kind, string Identity);
