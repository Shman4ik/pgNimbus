using PgNimbus.Core.Query;

namespace PgNimbus.Core.Security;

/// <summary>
/// One <c>pg_default_acl</c> entry as the <c>ALTER DEFAULT PRIVILEGES</c>
/// statement it stands for, ready to be edited into a REVOKE or re-pointed at
/// another creating role.
///
/// Pure, like <see cref="GrantScriptBuilder"/>: identifiers go through
/// <see cref="SqlIdentifier.QuoteIfNeeded"/>, the grantee through
/// <see cref="GrantScriptBuilder.GranteeSql"/> (<c>null</c> is the
/// <c>PUBLIC</c> keyword; a role named PUBLIC is <c>"PUBLIC"</c>), and the
/// names in the leading comment through <see cref="SqlComment.Safe"/>.
/// </summary>
public static class DefaultPrivilegeScriptBuilder
{
    private const string Newline = "\n";

    /// <summary>The <c>ON …</c> keyword of the statement — plural, unlike <c>GRANT</c>'s.</summary>
    public static string ObjectClassKeyword(SecurableKind kind) => kind switch
    {
        SecurableKind.Table => "TABLES",
        SecurableKind.Sequence => "SEQUENCES",
        SecurableKind.Function => "FUNCTIONS",
        SecurableKind.Type => "TYPES",
        SecurableKind.Schema => "SCHEMAS",
        _ => kind.ToString().ToUpperInvariant(),
    };

    /// <summary>
    /// The statement, under the two comment lines that are the two things
    /// people get wrong about default privileges (they do not touch existing
    /// objects, and they key off the creating role), kept with the SQL because
    /// that is where they will be read.
    /// </summary>
    /// <param name="schema">Null for the database-wide default (<c>defaclnamespace = 0</c>).</param>
    /// <param name="grantee">Null for PUBLIC.</param>
    public static string Build(
        string ownerRole,
        string? schema,
        SecurableKind appliesTo,
        string? grantee,
        bool withGrantOption,
        IReadOnlyList<PrivilegeKind> privileges)
    {
        var objectClass = ObjectClassKeyword(appliesTo);
        var owner = SqlIdentifier.QuoteIfNeeded(ownerRole);
        var scope = schema is null ? "" : $" IN SCHEMA {SqlIdentifier.QuoteIfNeeded(schema)}";
        var option = withGrantOption ? " WITH GRANT OPTION" : "";
        var privilegeList = string.Join(", ", privileges.Select(Privileges.Sql));

        // The names are inside a comment, where quoting protects nothing and a
        // line break would end the line and leave a statement behind it
        // (finding 11) — so they go through SqlComment.Safe.
        var where = schema is null ? "in any schema" : $"in schema {SqlComment.Safe(schema)}";

        return string.Join(Newline,
        [
            $"-- Applies to {objectClass.ToLowerInvariant()} created from now on by {SqlComment.Safe(ownerRole)} {where}.",
            "-- Objects that already exist are untouched: those need",
            $"-- GRANT … ON ALL {objectClass} IN SCHEMA …, which this does not replace.",
            "--",
            "-- The key is the CREATING role, not the schema. Pointed at the wrong creator",
            "-- this statement runs fine and does nothing.",
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {owner}{scope}",
            $"    GRANT {privilegeList} ON {objectClass} TO {GrantScriptBuilder.GranteeSql(grantee)}{option};",
        ]);
    }
}
