using System.Text;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Security;

/// <summary>
/// The role the user asked for, as the editor describes it. The desired state,
/// not a diff — <see cref="RoleScriptBuilder.Alter"/> does the diffing against
/// the <see cref="RoleAttributes"/> the catalog reported.
/// </summary>
/// <param name="ConnectionLimit">
/// Null means "no limit", which Postgres writes as <c>-1</c> and which
/// <see cref="RoleScriptBuilder.Create"/> expresses by omitting the clause
/// entirely.
/// </param>
/// <param name="ValidUntil">Null means the role never expires.</param>
/// <param name="MemberOf">The groups this role should belong to.</param>
public sealed record RoleDefinition(
    string Name,
    bool CanLogin,
    bool IsSuperuser,
    bool Inherit,
    bool CanCreateDb,
    bool CanCreateRole,
    bool CanReplicate,
    bool BypassRls,
    int? ConnectionLimit,
    DateTimeOffset? ValidUntil,
    IReadOnlyList<string> MemberOf,
    string? Comment);

/// <summary>
/// Generates the role DDL the user reviews before it runs — create, alter,
/// rename, membership, password, and the drop recipe Postgres's own error
/// message refuses to give.
///
/// Pure: no catalog access, no Npgsql. Identifiers go through
/// <see cref="SqlIdentifier.QuoteIfNeeded"/> and literals through
/// <see cref="SqlLiteral"/>; nothing is concatenated raw.
/// </summary>
public static class RoleScriptBuilder
{
    private const string Newline = "\n";

    /// <summary>
    /// What a masked password renders as. Four bullets, not the real length —
    /// the length of a password is itself worth not leaking into a screenshot.
    /// </summary>
    private const string Mask = "••••";

    /// <summary>
    /// The <c>CREATE ROLE</c> statement, plus a <c>GRANT</c> per
    /// <see cref="RoleDefinition.MemberOf"/> entry and a <c>COMMENT ON ROLE</c>
    /// when a comment is set.
    ///
    /// <para><paramref name="maskPassword"/> is a security property, not a
    /// formatting option. The same call produces the script the UI *shows* and
    /// the script it *runs*: the shown one passes true and renders
    /// <c>PASSWORD '••••'</c>, the executed one passes false. That is what
    /// keeps the real literal out of the preview pane, out of a screenshot, and
    /// — via <see cref="SecretRedactor"/> on the executed text — out of the
    /// query history file and the crash log.</para>
    ///
    /// <para>The negative keywords (<c>NOSUPERUSER</c>, <c>NOCREATEDB</c>, …)
    /// are always written out rather than left to the server's defaults, so the
    /// generated script is a complete statement of intent that says the same
    /// thing wherever it is pasted.</para>
    /// </summary>
    public static string Create(RoleDefinition definition, string? password, bool maskPassword = false)
    {
        var name = SqlIdentifier.QuoteIfNeeded(definition.Name);
        var sb = new StringBuilder();

        sb.Append("CREATE ROLE ").Append(name).Append(" WITH ");
        sb.Append(string.Join(' ', AttributeKeywords(definition)));

        if (definition.ConnectionLimit is { } limit)
        {
            sb.Append(" CONNECTION LIMIT ").Append(limit);
        }

        if (definition.ValidUntil is { } until)
        {
            sb.Append(" VALID UNTIL ").Append(SqlLiteral.Format(until));
        }

        if (password is not null)
        {
            sb.Append(" PASSWORD ").Append(RenderPassword(password, maskPassword));
        }

        sb.Append(';');

        foreach (var group in definition.MemberOf)
        {
            sb.Append(Newline).Append(AddMember(group, definition.Name));
        }

        if (definition.Comment is not null)
        {
            sb.Append(Newline).Append(CommentOn(definition.Name, definition.Comment));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The statements that move <paramref name="current"/> to
    /// <paramref name="desired"/>, and nothing else — an empty string when the
    /// two already agree. Emitting the full <c>ALTER ROLE</c> every time would
    /// re-assert attributes nobody touched, which reads as a bigger change than
    /// it is and makes the review pane useless.
    ///
    /// <para>Attribute changes collapse into a single
    /// <c>ALTER ROLE … WITH …</c>; a changed comment becomes
    /// <c>COMMENT ON ROLE</c>; membership differences become
    /// <c>GRANT</c>/<c>REVOKE</c>.</para>
    ///
    /// <para><paramref name="currentMemberOf"/> is separate because
    /// <see cref="RoleAttributes"/> does not carry memberships — they come from
    /// <c>pg_auth_members</c>, not <c>pg_roles</c>. Null means "not known
    /// here", and membership is then left alone rather than guessed at: with an
    /// empty list assumed, every desired group would be re-granted and nothing
    /// would ever be revoked.</para>
    ///
    /// <para>A renamed role is out of scope — <see cref="Rename"/> is its own
    /// statement, and <see cref="RoleAttributes.Name"/> is what every statement
    /// here targets.</para>
    /// </summary>
    public static string Alter(
        RoleAttributes current,
        RoleDefinition desired,
        IReadOnlyList<string>? currentMemberOf = null)
    {
        var name = SqlIdentifier.QuoteIfNeeded(current.Name);
        var statements = new List<string>();
        var changed = new List<string>();

        void Flag(bool now, bool then, string yes, string no)
        {
            if (now != then)
            {
                changed.Add(then ? yes : no);
            }
        }

        Flag(current.CanLogin, desired.CanLogin, "LOGIN", "NOLOGIN");
        Flag(current.IsSuperuser, desired.IsSuperuser, "SUPERUSER", "NOSUPERUSER");
        Flag(current.Inherit, desired.Inherit, "INHERIT", "NOINHERIT");
        Flag(current.CanCreateDb, desired.CanCreateDb, "CREATEDB", "NOCREATEDB");
        Flag(current.CanCreateRole, desired.CanCreateRole, "CREATEROLE", "NOCREATEROLE");
        Flag(current.CanReplicate, desired.CanReplicate, "REPLICATION", "NOREPLICATION");
        Flag(current.BypassRls, desired.BypassRls, "BYPASSRLS", "NOBYPASSRLS");

        // Postgres stores "no limit" as -1, and that is what a null desired
        // limit means — so clearing a limit is a real change, not a no-op.
        var desiredLimit = desired.ConnectionLimit ?? -1;
        if (desiredLimit != current.ConnectionLimit)
        {
            changed.Add($"CONNECTION LIMIT {desiredLimit}");
        }

        if (desired.ValidUntil != current.ValidUntil)
        {
            // 'infinity' is how an expiry is removed; there is no "NO VALID UNTIL".
            changed.Add(desired.ValidUntil is { } until
                ? $"VALID UNTIL {SqlLiteral.Format(until)}"
                : "VALID UNTIL 'infinity'");
        }

        if (changed.Count > 0)
        {
            statements.Add($"ALTER ROLE {name} WITH {string.Join(' ', changed)};");
        }

        if (currentMemberOf is not null)
        {
            var have = new HashSet<string>(currentMemberOf, StringComparer.Ordinal);
            var want = new HashSet<string>(desired.MemberOf, StringComparer.Ordinal);

            foreach (var group in desired.MemberOf.Where(g => !have.Contains(g)).Distinct(StringComparer.Ordinal))
            {
                statements.Add(AddMember(group, current.Name));
            }

            foreach (var group in currentMemberOf.Where(g => !want.Contains(g)).Distinct(StringComparer.Ordinal))
            {
                statements.Add(RemoveMember(group, current.Name));
            }
        }

        if (desired.Comment != current.Comment)
        {
            statements.Add(CommentOn(current.Name, desired.Comment));
        }

        return string.Join(Newline, statements);
    }

    /// <summary>
    /// <c>ALTER ROLE … WITH PASSWORD '…'</c>. <paramref name="maskPassword"/>
    /// carries the same meaning as on <see cref="Create"/>: the preview gets
    /// the mask, the execution gets the literal.
    /// </summary>
    public static string SetPassword(string role, string password, bool maskPassword = false) =>
        $"ALTER ROLE {SqlIdentifier.QuoteIfNeeded(role)} WITH PASSWORD {RenderPassword(password, maskPassword)};";

    /// <summary>Renames a role. Grants and ownership follow the OID, so nothing else has to change.</summary>
    public static string Rename(string from, string to) =>
        $"ALTER ROLE {SqlIdentifier.QuoteIfNeeded(from)} RENAME TO {SqlIdentifier.QuoteIfNeeded(to)};";

    /// <summary>Makes <paramref name="member"/> a member of <paramref name="group"/>.</summary>
    public static string AddMember(string group, string member) =>
        $"GRANT {SqlIdentifier.QuoteIfNeeded(group)} TO {SqlIdentifier.QuoteIfNeeded(member)};";

    /// <summary>Removes <paramref name="member"/> from <paramref name="group"/>.</summary>
    public static string RemoveMember(string group, string member) =>
        $"REVOKE {SqlIdentifier.QuoteIfNeeded(group)} FROM {SqlIdentifier.QuoteIfNeeded(member)};";

    /// <summary>
    /// The drop recipe, commented, in the only order that works.
    ///
    /// <para><c>DROP ROLE</c> fails with <c>2BP01</c> the moment the role owns
    /// anything or holds any grant, and the server's error names neither the
    /// objects nor the fix. <c>REASSIGN OWNED BY</c> hands the objects over;
    /// <c>DROP OWNED BY</c> then clears the privileges reassignment leaves
    /// behind; only then does the drop succeed. Both act on the current
    /// database only, which is the part users find out about one database at a
    /// time — so the script says so.</para>
    ///
    /// <para>With <paramref name="reassignTo"/> null the <c>REASSIGN</c> line
    /// is omitted and the comment says plainly that <c>DROP OWNED</c> then
    /// <em>deletes</em> the owned objects instead of transferring them. That
    /// difference is the entire reason the parameter exists, and it is not
    /// recoverable.</para>
    /// </summary>
    public static string Drop(string role, string? reassignTo, bool grantMembershipFirst = false)
    {
        var name = SqlIdentifier.QuoteIfNeeded(role);
        var display = CommentSafe(role);
        var sb = new StringBuilder();

        sb.Append("-- \"").Append(display).Append("\" may own objects or hold grants, and DROP ROLE alone").Append(Newline);
        sb.Append("-- then fails with 2BP01 without naming either the objects or the fix.").Append(Newline);

        // REASSIGN OWNED and DROP OWNED are not superuser-only, but they do
        // require the executing role to hold the privileges of the role being
        // emptied (and of the one taking over), or the server answers 42501
        // "permission denied to reassign objects". A superuser has them
        // implicitly; on RDS, Neon or Supabase nobody is, so the membership has
        // to be granted first -- and since CREATEROLE is what let the user make
        // this role in the first place, it is also what lets them grant it.
        if (grantMembershipFirst)
        {
            sb.Append(reassignTo is null
                    ? "-- DROP OWNED requires the privileges of the role being"
                    : "-- REASSIGN OWNED and DROP OWNED require the privileges of the role being")
                .Append(Newline);
            sb.Append("-- emptied. Without them the server answers 42501, however many objects the").Append(Newline);
            sb.Append("-- role owns. A superuser holds them implicitly; otherwise grant the").Append(Newline);
            sb.Append("-- membership to yourself first -- it is revoked again below.").Append(Newline);
            sb.Append("GRANT ").Append(name).Append(" TO CURRENT_USER;").Append(Newline);

            if (reassignTo is not null)
            {
                sb.Append("GRANT ").Append(SqlIdentifier.QuoteIfNeeded(reassignTo)).Append(" TO CURRENT_USER;").Append(Newline);
            }
        }

        if (reassignTo is not null)
        {
            sb.Append("-- REASSIGN OWNED hands the objects to another role; DROP OWNED then removes").Append(Newline);
            sb.Append("-- the privileges REASSIGN OWNED leaves behind. Both act on the current").Append(Newline);
            sb.Append("-- database only -- repeat them while connected to every other database that").Append(Newline);
            sb.Append("-- contains objects owned by this role, then drop it.").Append(Newline);
            sb.Append("REASSIGN OWNED BY ").Append(name).Append(" TO ")
                .Append(SqlIdentifier.QuoteIfNeeded(reassignTo)).Append(';').Append(Newline);
        }
        else
        {
            sb.Append("-- No role was named to take the objects over, so DROP OWNED will DELETE").Append(Newline);
            sb.Append("-- everything \"").Append(display).Append("\" owns -- tables, schemas, data -- rather than hand").Append(Newline);
            sb.Append("-- it over. Name a role to reassign to first if the objects should be kept.").Append(Newline);
            sb.Append("-- DROP OWNED acts on the current database only -- repeat it while connected").Append(Newline);
            sb.Append("-- to every other database that contains objects owned by this role, then").Append(Newline);
            sb.Append("-- drop it.").Append(Newline);
        }

        sb.Append("DROP OWNED BY ").Append(name).Append(';').Append(Newline);

        // DROP ROLE takes the membership in the dropped role with it, but a
        // membership granted in the *target* role outlives this script and has
        // to be given back, or the drop quietly leaves the user holding
        // privileges they did not have before.
        if (grantMembershipFirst && reassignTo is not null)
        {
            sb.Append("REVOKE ").Append(SqlIdentifier.QuoteIfNeeded(reassignTo)).Append(" FROM CURRENT_USER;").Append(Newline);
        }

        sb.Append("DROP ROLE ").Append(name).Append(';');

        return sb.ToString();
    }

    /// <summary>
    /// Every attribute keyword, positive or negative — no attribute is left to
    /// the server default.
    /// </summary>
    private static IEnumerable<string> AttributeKeywords(RoleDefinition d)
    {
        yield return d.CanLogin ? "LOGIN" : "NOLOGIN";
        yield return d.IsSuperuser ? "SUPERUSER" : "NOSUPERUSER";
        yield return d.Inherit ? "INHERIT" : "NOINHERIT";
        yield return d.CanCreateDb ? "CREATEDB" : "NOCREATEDB";
        yield return d.CanCreateRole ? "CREATEROLE" : "NOCREATEROLE";
        yield return d.CanReplicate ? "REPLICATION" : "NOREPLICATION";
        yield return d.BypassRls ? "BYPASSRLS" : "NOBYPASSRLS";
    }

    private static string CommentOn(string role, string? comment) =>
        $"COMMENT ON ROLE {SqlIdentifier.QuoteIfNeeded(role)} IS {(comment is null ? "NULL" : SqlLiteral.Quote(comment))};";

    private static string RenderPassword(string password, bool mask) =>
        SqlLiteral.Quote(mask ? Mask : password);

    /// <summary>
    /// Strips the characters that would let a name break out of a <c>--</c>
    /// comment. A role name can legally contain a newline; the recipe comment
    /// is the one place in this file where a name is not inside a quoted
    /// identifier, so it is the one place that has to defend itself.
    /// </summary>
    private static string CommentSafe(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ');
}
