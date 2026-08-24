using System.Text;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Security;

/// <summary>
/// One cell of the permissions grid the user toggled: a single privilege on a
/// single object for a single grantee, in one direction.
/// </summary>
/// <param name="Grantee">The role name, or null for <c>PUBLIC</c>.</param>
/// <param name="Grant">True for <c>GRANT</c>, false for <c>REVOKE</c>.</param>
/// <param name="WithGrantOption">
/// On a grant, appends <c>WITH GRANT OPTION</c>. On a revoke it means the
/// narrower <c>REVOKE GRANT OPTION FOR …</c> — take away the right to re-grant
/// but leave the privilege itself, which is a different statement, not a
/// suffix.
/// </param>
/// <param name="Column">A column name for a column-level grant; null for the whole object.</param>
public sealed record PrivilegeChange(
    SecurableRef Object,
    string? Grantee,
    PrivilegeKind Privilege,
    bool Grant,
    bool WithGrantOption = false,
    string? Column = null);

/// <summary>The shape of a whole-schema grant, in the words users ask for it.</summary>
public enum BulkGrantPreset
{
    /// <summary>Read the data: SELECT on tables, USAGE+SELECT on sequences.</summary>
    ReadOnly,

    /// <summary>Read and change the data, but not the schema.</summary>
    ReadWrite,

    /// <summary>Everything the schema and its contents can grant, including CREATE on the schema.</summary>
    Full,

    /// <summary>Take it all back, including the default privileges a grant script set up.</summary>
    RevokeAll,
}

/// <summary>
/// "Give this role read access to this schema", expressed once.
/// </summary>
/// <param name="Grantee">The role name, or null for <c>PUBLIC</c>.</param>
/// <param name="IncludeFutureObjects">
/// Also emit the <c>ALTER DEFAULT PRIVILEGES</c> statements, without which the
/// grant stops applying at the next migration.
/// </param>
/// <param name="FutureObjectsOwner">
/// The role that will *create* the future objects. A default privilege is keyed
/// to the creating role, not to the schema, so this cannot be inferred and
/// naming the wrong one silently does nothing.
/// </param>
public sealed record BulkGrantRequest(
    string Schema,
    string? Grantee,
    BulkGrantPreset Preset,
    bool IncludeFutureObjects,
    string? FutureObjectsOwner);

/// <summary>
/// Turns permission edits into the <c>GRANT</c>/<c>REVOKE</c> script the user
/// reviews before it runs. Deliberately a script and not a hidden write, same
/// precedent as <see cref="Schema.DdlTemplates"/>: the statement in the editor
/// can be read, edited and kept, and a form that can only express what its
/// checkboxes list is a worse tool than the SQL.
///
/// Pure — no catalog access, no Npgsql. Every identifier goes through
/// <see cref="SqlIdentifier.QuoteIfNeeded"/> and every literal through
/// <see cref="SqlLiteral.Quote"/>; nothing is concatenated raw. Output is
/// deterministic for a given change set regardless of the order the UI
/// collected the edits in, because the script is compared in tests and in
/// screenshots.
/// </summary>
public static class GrantScriptBuilder
{
    /// <summary>The pseudo-role every role is a member of. A keyword — never quoted.</summary>
    public const string PublicGrantee = "PUBLIC";

    private const string Newline = "\n";

    /// <summary>
    /// Collapses <paramref name="changes"/> into the fewest statements that say
    /// the same thing: one statement per (object, columns, grantee, direction,
    /// grant-option), with the privileges comma-joined in
    /// <see cref="Privileges.For"/> order.
    ///
    /// <para>REVOKEs are emitted before GRANTs. A script that grants and then
    /// revokes the same privilege is a different script from one that revokes
    /// and then grants, and these get edited by hand — so the order is fixed
    /// rather than inherited from whatever order the grid was clicked in.</para>
    ///
    /// <para>Returns an empty string for an empty change set.</para>
    /// </summary>
    public static string Build(IReadOnlyList<PrivilegeChange> changes)
    {
        if (changes.Count == 0)
        {
            return string.Empty;
        }

        // Step 1: one bucket of privileges per grid cell.
        var cells = changes
            .GroupBy(c => new CellKey(c.Object, c.Grantee, c.Grant, c.WithGrantOption, c.Column))
            .Select(g => new Cell(g.Key, SortPrivileges(g.Select(c => c.Privilege), OrderFor(g.Key))))
            .ToList();

        // Step 2: column cells that carry the identical privilege set merge into
        // one statement with a shared column list — GRANT SELECT (id, email) …
        // rather than two single-column statements.
        var emitted = new List<Statement>();
        foreach (var group in cells.GroupBy(c => new MergeKey(
                     c.Key.Object,
                     c.Key.Grantee,
                     c.Key.Grant,
                     c.Key.WithGrantOption,
                     c.Key.Column is null,
                     Signature(c.Privileges))))
        {
            var key = group.First().Key;
            var privileges = group.First().Privileges;
            var columns = key.Column is null
                ? null
                : group.Select(c => c.Key.Column!)
                    .Distinct(StringComparer.Ordinal)
                    // Ordinal, not input order: a shuffled change list has to
                    // produce a byte-identical script.
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList();

            emitted.Add(new Statement(
                key.Grant,
                key.Object.Display,
                columns is null ? string.Empty : string.Join(",", columns),
                Label(key.Grantee),
                key.WithGrantOption,
                RenderStatement(key, privileges, columns)));
        }

        var ordered = emitted
            .OrderBy(s => s.Grant)                                     // false (REVOKE) first
            .ThenBy(s => s.ObjectDisplay, StringComparer.Ordinal)
            .ThenBy(s => s.ColumnKey, StringComparer.Ordinal)          // object-level ("") before column-level
            .ThenBy(s => s.GranteeLabel, StringComparer.Ordinal)
            .ThenBy(s => s.WithGrantOption)
            .Select(s => s.Text);

        return string.Join(Newline, ordered);
    }

    /// <summary>
    /// The whole-schema grant, and the reason this class exists: the three
    /// things pgAdmin's Grant Wizard gets wrong are handled here rather than
    /// left to the user to discover from a <c>permission denied</c>.
    ///
    /// <list type="number">
    /// <item><c>GRANT USAGE ON SCHEMA</c> is the first statement of every
    /// non-revoke preset. Granting every table privilege without it leaves the
    /// role unable to reach any of them (pgadmin4#8954).</item>
    /// <item><c>ON ALL TABLES IN SCHEMA</c> only covers what exists right now,
    /// which the script says in a comment, and
    /// <see cref="BulkGrantRequest.IncludeFutureObjects"/> follows the grants
    /// with the matching <c>ALTER DEFAULT PRIVILEGES</c>.</item>
    /// <item>Revoking is a preset, not an afterthought — pgAdmin's wizard
    /// cannot revoke at all (pgadmin4#7891) — and it undoes the default
    /// privileges too, in the reverse order of the grant script.</item>
    /// </list>
    /// </summary>
    public static string BuildBulk(BulkGrantRequest request)
    {
        var schema = SqlIdentifier.QuoteIfNeeded(request.Schema);
        var grantee = Label(request.Grantee);
        var revoking = request.Preset == BulkGrantPreset.RevokeAll;
        var owner = string.IsNullOrWhiteSpace(request.FutureObjectsOwner)
            ? null
            : SqlIdentifier.QuoteIfNeeded(request.FutureObjectsOwner!);
        var withDefaults = request.IncludeFutureObjects && owner is not null;

        var sb = new StringBuilder();

        void Line(string text) => sb.Append(text).Append(Newline);

        // -- Comments first: everything below is a statement the user may edit,
        //    and the traps are why the script looks the way it does.
        Line(revoking
            ? "-- REVOKE ... ON ALL TABLES IN SCHEMA reaches only the objects that exist"
            : "-- GRANT ... ON ALL TABLES IN SCHEMA reaches only the objects that exist");
        Line("-- right now. Anything created afterwards is not covered by these statements.");

        if (request.IncludeFutureObjects)
        {
            Line("-- ALTER DEFAULT PRIVILEGES is keyed to the role that CREATES an object, not");
            Line("-- to the schema the object lands in. Set for the wrong creator it silently");
            Line("-- does nothing, which is why the owner has to be named.");

            if (owner is null)
            {
                Line("-- No creating role was named, so the ALTER DEFAULT PRIVILEGES statements are");
                Line("-- omitted rather than guessed at.");
            }
        }

        if (request.Preset == BulkGrantPreset.ReadOnly)
        {
            Line("-- On PostgreSQL 14+ a cluster-wide read-only role is one membership instead");
            Line($"-- of this whole script: GRANT pg_read_all_data TO {grantee}; the role editor");
            Line("-- offers it.");
        }

        var objectClasses = ObjectClassesFor(request.Preset);

        if (revoking)
        {
            // Exactly the reverse of the grant script: stop the bleeding for
            // future objects first, then existing ones, then the schema itself.
            if (withDefaults)
            {
                foreach (var (objectClass, _) in objectClasses.AsEnumerable().Reverse())
                {
                    Line($"ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema} REVOKE ALL PRIVILEGES ON {objectClass} FROM {grantee};");
                }
            }

            foreach (var (objectClass, _) in objectClasses.AsEnumerable().Reverse())
            {
                Line($"REVOKE ALL PRIVILEGES ON ALL {objectClass} IN SCHEMA {schema} FROM {grantee};");
            }

            Line($"REVOKE ALL PRIVILEGES ON SCHEMA {schema} FROM {grantee};");
            return sb.ToString().TrimEnd('\n');
        }

        // Rule 1: USAGE on the schema, before anything else, always.
        Line($"GRANT USAGE ON SCHEMA {schema} TO {grantee};");

        if (request.Preset == BulkGrantPreset.Full)
        {
            // Deliberately a second statement rather than "USAGE, CREATE": the
            // combined form collapses to ALL PRIVILEGES on a two-privilege
            // schema ACL and hides the USAGE grant this whole method exists to
            // keep visible.
            Line($"GRANT CREATE ON SCHEMA {schema} TO {grantee};");
        }

        foreach (var (objectClass, privileges) in objectClasses)
        {
            Line($"GRANT {privileges} ON ALL {objectClass} IN SCHEMA {schema} TO {grantee};");
        }

        if (withDefaults)
        {
            foreach (var (objectClass, privileges) in objectClasses)
            {
                Line($"ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema} GRANT {privileges} ON {objectClass} TO {grantee};");
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The grantee as it is written in SQL: a keyword for PUBLIC, a quoted identifier otherwise.</summary>
    private static string Label(string? grantee) =>
        grantee is null || grantee.Equals(PublicGrantee, StringComparison.OrdinalIgnoreCase)
            ? PublicGrantee
            : SqlIdentifier.QuoteIfNeeded(grantee);

    private static string RenderStatement(CellKey key, IReadOnlyList<PrivilegeKind> privileges, IReadOnlyList<string>? columns)
    {
        var list = RenderPrivileges(privileges, OrderFor(key), columns);
        var target = key.Object.GrantTarget;
        var grantee = Label(key.Grantee);

        if (key.Grant)
        {
            var suffix = key.WithGrantOption ? " WITH GRANT OPTION" : string.Empty;
            return $"GRANT {list} ON {target} TO {grantee}{suffix};";
        }

        // REVOKE has no WITH GRANT OPTION suffix — the narrower "take back the
        // right to re-grant, keep the privilege" is a prefix and a different
        // statement.
        var prefix = key.WithGrantOption ? "GRANT OPTION FOR " : string.Empty;
        return $"REVOKE {prefix}{list} ON {target} FROM {grantee};";
    }

    /// <summary>
    /// The privilege list, with an optional column list attached to each entry.
    /// A set that covers every privilege in <paramref name="order"/> collapses
    /// to <c>ALL PRIVILEGES</c>.
    ///
    /// <para>That collapse is cosmetic, not semantic: <c>ALL PRIVILEGES</c> is
    /// resolved by the server at execution time, so on a newer server it can
    /// mean strictly more than the list it replaced (MAINTAIN arrived in PG17
    /// and joined "all" for tables). It is emitted only when the change set
    /// already covers everything this build knows about, so it never *narrows*
    /// what was asked for — but a script kept and re-run against a newer server
    /// may grant more than it did the first time.</para>
    /// </summary>
    private static string RenderPrivileges(
        IReadOnlyList<PrivilegeKind> privileges,
        IReadOnlyList<PrivilegeKind> order,
        IReadOnlyList<string>? columns)
    {
        var suffix = columns is null || columns.Count == 0
            ? string.Empty
            : $" ({string.Join(", ", columns.Select(SqlIdentifier.QuoteIfNeeded))})";

        if (order.Count > 0 && privileges.Count == order.Count && order.All(privileges.Contains))
        {
            return AllPrivileges + suffix;
        }

        return string.Join(", ", privileges.Select(p => Privileges.Sql(p) + suffix));
    }

    private static IReadOnlyList<PrivilegeKind> OrderFor(CellKey key) =>
        key.Column is null ? Privileges.For(key.Object.Kind) : Privileges.ForColumn();

    /// <summary>Position of <paramref name="privilege"/> in <paramref name="order"/>, or -1.</summary>
    private static int IndexOf(IReadOnlyList<PrivilegeKind> order, PrivilegeKind privilege)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == privilege)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Privileges in <see cref="Privileges.For"/> order — the order the
    /// permissions matrix shows its columns in — not the order the edits
    /// arrived in. Anything outside that list (a privilege the caller asked for
    /// that does not apply to this object class) is kept and sorted last rather
    /// than dropped, so a wrong change surfaces as wrong SQL instead of
    /// disappearing.
    /// </summary>
    private static IReadOnlyList<PrivilegeKind> SortPrivileges(
        IEnumerable<PrivilegeKind> privileges,
        IReadOnlyList<PrivilegeKind> order) =>
        privileges
            .Distinct()
            .OrderBy(p => IndexOf(order, p) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(p => (int)p)
            .ToList();

    private static string Signature(IReadOnlyList<PrivilegeKind> privileges) =>
        string.Join(",", privileges.Select(p => ((int)p).ToString()));

    /// <summary>
    /// The object classes a preset touches, in grant order, with the privilege
    /// list already rendered. Privileges are listed in
    /// <see cref="Privileges.For"/> order (so a sequence reads USAGE, SELECT,
    /// UPDATE) — the same order the permissions grid shows its columns in and
    /// the same order <see cref="Build"/> emits, rather than a second ordering
    /// convention for the bulk path.
    ///
    /// <para>Full and RevokeAll deliberately say <c>ALL PRIVILEGES</c> rather
    /// than spelling the list out: those presets mean "whatever this server
    /// can grant here", which is exactly what the server resolves
    /// <c>ALL PRIVILEGES</c> to — including privileges added after this build
    /// (MAINTAIN in PG17). The narrower presets spell their lists out for the
    /// opposite reason: a read-only role must not silently widen.</para>
    /// </summary>
    private static IReadOnlyList<(string ObjectClass, string Privileges)> ObjectClassesFor(
        BulkGrantPreset preset) => preset switch
    {
        BulkGrantPreset.ReadOnly =>
        [
            ("TABLES", Join(PrivilegeKind.Select)),
            ("SEQUENCES", Join(PrivilegeKind.Usage, PrivilegeKind.Select)),
        ],
        BulkGrantPreset.ReadWrite =>
        [
            ("TABLES", Join(PrivilegeKind.Select, PrivilegeKind.Insert, PrivilegeKind.Update, PrivilegeKind.Delete)),
            ("SEQUENCES", Join(PrivilegeKind.Usage, PrivilegeKind.Select, PrivilegeKind.Update)),
        ],
        BulkGrantPreset.Full or BulkGrantPreset.RevokeAll =>
        [
            ("TABLES", AllPrivileges),
            ("SEQUENCES", AllPrivileges),
            ("FUNCTIONS", AllPrivileges),
        ],
        _ => [],
    };

    private const string AllPrivileges = "ALL PRIVILEGES";

    private static string Join(params PrivilegeKind[] privileges) =>
        string.Join(", ", privileges.Select(Privileges.Sql));

    private sealed record CellKey(
        SecurableRef Object,
        string? Grantee,
        bool Grant,
        bool WithGrantOption,
        string? Column);

    private sealed record Cell(CellKey Key, IReadOnlyList<PrivilegeKind> Privileges);

    private sealed record MergeKey(
        SecurableRef Object,
        string? Grantee,
        bool Grant,
        bool WithGrantOption,
        bool ObjectLevel,
        string PrivilegeSignature);

    private sealed record Statement(
        bool Grant,
        string ObjectDisplay,
        string ColumnKey,
        string GranteeLabel,
        bool WithGrantOption,
        string Text);
}
