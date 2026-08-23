using Npgsql;

namespace PgNimbus.Core.Security;

/// <summary>
/// Read-only catalog access for roles: attributes, membership edges, and the
/// objects that would make <c>DROP ROLE</c> fail. Every query here is deliberately
/// runnable by an ordinary, non-superuser login — it reads <c>pg_roles</c>, which
/// is world-readable, and never <c>pg_authid</c>, which needs superuser and whose
/// only extra column is the password hash this app has no business reading. That
/// matters because on RDS, Neon and Supabase nobody is superuser, and a roles
/// panel that assumes otherwise shows an error where perfectly readable data was
/// available.
/// </summary>
public sealed class RoleService
{
    /// <summary>
    /// Cap on <see cref="GetGrantsHeldAsync"/>. A role granted on 50k tables must
    /// not hang the drop dialog; the caller reports a full page as "at least this
    /// many" rather than pretending it is the whole list.
    /// </summary>
    public const int GrantsHeldLimit = 200;

    private readonly NpgsqlDataSource _dataSource;

    public RoleService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Every role on the server, alphabetically. <paramref name="includePredefined"/>
    /// false drops the built-in <c>pg_*</c> roles, matching
    /// <c>SchemaService.GetRolesAsync</c>'s filter — the schema tree lists roles a
    /// person created, not the ones initdb did.
    /// </summary>
    public async Task<IReadOnlyList<RoleAttributes>> GetRolesAsync(bool includePredefined, CancellationToken ct)
    {
        // The predefined filter rides a parameter rather than two SQL texts so the
        // thirteen columns are spelled out once. 'pg\_%' escapes the underscore —
        // an unescaped one is LIKE's single-character wildcard.
        const string sql = """
            SELECT r.oid, r.rolname, r.rolcanlogin, r.rolsuper, r.rolinherit,
                   r.rolcreaterole, r.rolcreatedb, r.rolreplication, r.rolbypassrls,
                   r.rolconnlimit, r.rolvaliduntil, r.rolconfig,
                   pg_catalog.shobj_description(r.oid, 'pg_authid')
            FROM pg_catalog.pg_roles r
            WHERE @includePredefined OR r.rolname NOT LIKE 'pg\_%'
            ORDER BY r.rolname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("includePredefined", includePredefined);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RoleAttributes>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RoleAttributes(
                reader.GetFieldValue<uint>(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetInt32(9),
                ReadValidUntil(reader, 10),
                reader.IsDBNull(11) ? [] : reader.GetFieldValue<string[]>(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return results;
    }

    /// <summary>
    /// Every edge of the role membership graph — who is a member of what — with
    /// the grant options that decide whether the membership actually carries
    /// privileges.
    /// </summary>
    public async Task<IReadOnlyList<RoleMembership>> GetMembershipsAsync(CancellationToken ct)
    {
        // PG16 split the single admin_option column into admin/inherit/set options.
        // On older servers those columns do not exist at all, and naming a missing
        // column is a hard parse error rather than a NULL — so this is two SQL
        // texts, not one with a COALESCE. The pre-16 fallbacks reproduce the old
        // semantics exactly: inheritance was a property of the *member* role
        // (rolinherit), and every membership could be assumed with SET ROLE.
        const string modernSql = """
            SELECT m.rolname, g.rolname, a.admin_option, a.inherit_option, a.set_option, gr.rolname
            FROM pg_catalog.pg_auth_members a
            JOIN pg_catalog.pg_roles m ON m.oid = a.member
            JOIN pg_catalog.pg_roles g ON g.oid = a.roleid
            LEFT JOIN pg_catalog.pg_roles gr ON gr.oid = a.grantor
            ORDER BY m.rolname, g.rolname
            """;

        const string legacySql = """
            SELECT m.rolname, g.rolname, a.admin_option, m.rolinherit, true, gr.rolname
            FROM pg_catalog.pg_auth_members a
            JOIN pg_catalog.pg_roles m ON m.oid = a.member
            JOIN pg_catalog.pg_roles g ON g.oid = a.roleid
            LEFT JOIN pg_catalog.pg_roles gr ON gr.oid = a.grantor
            ORDER BY m.rolname, g.rolname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var sql = PgFeatures.SupportsRoleMemberOptions(connection.PostgreSqlVersion) ? modernSql : legacySql;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RoleMembership>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RoleMembership(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    /// <summary>
    /// Everything in the <em>current database</em> that <paramref name="role"/>
    /// owns — the objects Postgres refuses a <c>DROP ROLE</c> over with 2BP01.
    /// Listing them before the user hits that error is what turns an opaque
    /// message into an actionable dialog (the fix is <c>REASSIGN OWNED BY</c>,
    /// per database). Ordered by kind, then name.
    /// </summary>
    public async Task<IReadOnlyList<RoleDependency>> GetOwnedObjectsAsync(string role, CancellationToken ct)
    {
        // pg_get_userbyid rather than a ::regrole cast on the parameter: it never
        // raises for an oid whose role has since been dropped, and the comparison
        // stays against the raw name the caller passed (regrole's text output
        // quotes anything that is not a bare lowercase identifier).
        //
        // Indexes and TOAST tables are excluded by the relkind list — they are
        // owned implicitly by their parent and dropping the parent takes them.
        // Array types and the row types Postgres creates behind every table are
        // excluded for the same reason: they are not independently droppable, so
        // listing them would pad the dialog with noise the user cannot act on.
        const string sql = """
            SELECT kind, identity FROM (
                SELECT CASE c.relkind
                           WHEN 'r' THEN 'table'
                           WHEN 'p' THEN 'partitioned table'
                           WHEN 'v' THEN 'view'
                           WHEN 'm' THEN 'materialized view'
                           WHEN 'S' THEN 'sequence'
                           WHEN 'f' THEN 'foreign table'
                       END AS kind,
                       n.nspname || '.' || c.relname AS identity
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
                  AND n.nspname <> 'pg_catalog'
                  AND n.nspname <> 'information_schema'
                  AND pg_catalog.pg_get_userbyid(c.relowner) = @role
                UNION ALL
                SELECT 'schema', n.nspname
                FROM pg_catalog.pg_namespace n
                WHERE n.nspname NOT LIKE 'pg\_%'
                  AND n.nspname <> 'information_schema'
                  AND pg_catalog.pg_get_userbyid(n.nspowner) = @role
                UNION ALL
                SELECT CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END,
                       n.nspname || '.' || p.proname
                           || '(' || pg_catalog.pg_get_function_arguments(p.oid) || ')'
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname <> 'pg_catalog'
                  AND n.nspname <> 'information_schema'
                  AND pg_catalog.pg_get_userbyid(p.proowner) = @role
                UNION ALL
                SELECT 'type', n.nspname || '.' || t.typname
                FROM pg_catalog.pg_type t
                JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
                WHERE n.nspname <> 'pg_catalog'
                  AND n.nspname <> 'information_schema'
                  AND (t.typrelid = 0
                       OR (SELECT c.relkind FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid) = 'c')
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type e
                                  WHERE e.oid = t.typelem AND e.typarray = t.oid)
                  AND pg_catalog.pg_get_userbyid(t.typowner) = @role
                UNION ALL
                SELECT 'database', d.datname
                FROM pg_catalog.pg_database d
                WHERE pg_catalog.pg_get_userbyid(d.datdba) = @role
            ) owned
            ORDER BY kind, identity
            """;

        return await ReadDependenciesAsync(sql, role, limit: null, ct);
    }

    /// <summary>
    /// Objects <paramref name="role"/> has been <em>granted</em> privileges on, in
    /// the current database. These are the other half of the drop recipe: an
    /// owned object needs <c>REASSIGN OWNED BY</c>, a held grant needs
    /// <c>DROP OWNED BY</c>, and a dialog that shows only the first leaves the
    /// user staring at the same 2BP01 after following its advice. Capped at
    /// <see cref="GrantsHeldLimit"/> rows.
    /// </summary>
    public async Task<IReadOnlyList<RoleDependency>> GetGrantsHeldAsync(string role, CancellationToken ct)
    {
        // aclexplode yields one row per privilege, so UNION (not UNION ALL) folds
        // a role holding SELECT+INSERT+UPDATE on one table into a single line.
        //
        // The grantee is matched by oid through a scalar subquery rather than the
        // more obvious grantee::regrole::text = @role: regrole's output function
        // applies quote_ident, so a role named "App User" renders as '"App User"'
        // and would silently match nothing. An unknown role name makes the
        // subquery NULL, which returns no rows — the right answer, and no error.
        const string sql = """
            SELECT kind, identity FROM (
                SELECT 'privilege on table' AS kind, n.nspname || '.' || c.relname AS identity
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) AS a
                WHERE c.relacl IS NOT NULL
                  AND n.nspname <> 'pg_catalog'
                  AND n.nspname <> 'information_schema'
                  AND a.grantee = (SELECT r.oid FROM pg_catalog.pg_roles r WHERE r.rolname = @role)
                UNION
                SELECT 'privilege on schema', n.nspname
                FROM pg_catalog.pg_namespace n
                CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) AS a
                WHERE n.nspacl IS NOT NULL
                  AND a.grantee = (SELECT r.oid FROM pg_catalog.pg_roles r WHERE r.rolname = @role)
            ) held
            ORDER BY kind, identity
            LIMIT @limit
            """;

        return await ReadDependenciesAsync(sql, role, GrantsHeldLimit, ct);
    }

    /// <summary>The role this connection is currently acting as — the "you are here" of every permission answer.</summary>
    public async Task<string> GetCurrentRoleAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT current_user", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return reader.GetString(0);
    }

    /// <summary>
    /// The server's version, for the <see cref="PgFeatures"/> gates — MAINTAIN is
    /// PG17+, the per-grant membership options are PG16+. Read from the handshake
    /// Npgsql already did, so this costs a pooled connection and no round trip.
    /// </summary>
    public async Task<Version> GetServerVersionAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return connection.PostgreSqlVersion;
    }

    private async Task<IReadOnlyList<RoleDependency>> ReadDependenciesAsync(
        string sql,
        string role,
        int? limit,
        CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("role", role);
        if (limit is { } cap)
        {
            command.Parameters.AddWithValue("limit", cap);
        }

        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RoleDependency>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RoleDependency(reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }

    /// <summary>
    /// <c>rolvaliduntil</c> is a timestamptz that is very often <c>infinity</c> —
    /// what <c>CREATE ROLE … VALID UNTIL 'infinity'</c> stores, and what some
    /// managed providers set by default. Npgsql surfaces that as
    /// <see cref="DateTime.MaxValue"/> (or throws, depending on the infinity
    /// conversion setting), and neither is something a DateTimeOffset column in
    /// the UI can show. "Valid forever" and "no expiry" mean the same thing to the
    /// user, so both collapse to null.
    /// </summary>
    private static DateTimeOffset? ReadValidUntil(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            var value = reader.GetFieldValue<DateTime>(ordinal);
            if (value == DateTime.MaxValue || value == DateTime.MinValue)
            {
                return null;
            }

            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or ArgumentOutOfRangeException or FormatException)
        {
            return null;
        }
    }
}
