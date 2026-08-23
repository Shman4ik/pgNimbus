using Npgsql;
using NpgsqlTypes;

namespace PgNimbus.Core.Security;

/// <summary>
/// Reads privileges out of pg_catalog: object ACLs, column ACLs, default
/// privileges, RLS state, and the authoritative <c>has_*_privilege()</c>
/// answers the resolver reconciles against.
///
/// Nothing here needs superuser. <c>pg_class.relacl</c>, <c>pg_namespace.nspacl</c>,
/// <c>pg_default_acl</c>, <c>pg_policy</c> and the <c>has_*_privilege()</c>
/// family are all readable by an ordinary role, which is the whole point —
/// on RDS, Neon and Supabase you never are superuser, and a permissions panel
/// that assumes otherwise shows an error where the (perfectly readable) answer
/// should be. Deliberately absent: anything that touches <c>pg_authid</c>.
///
/// Everything here is read-only, so it is always safe to run against production.
/// </summary>
public sealed class PrivilegeService
{
    private readonly NpgsqlDataSource _dataSource;

    public PrivilegeService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Catalog schemas are hidden from the object picker unless the caller asks
    /// for one by name — a picker that opens on four thousand pg_catalog entries
    /// is not a picker. Mirrors <c>SchemaService</c>'s exclusion.
    /// </summary>
    private const string HideSystemSchemas = """
                  AND (@schema IS NOT NULL
                       OR (n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'))
        """;

    // ---------------------------------------------------------------- picker

    /// <summary>
    /// The objects of one class that a privilege can be held on, name order —
    /// the object picker's list. <paramref name="schema"/> filters where it
    /// applies and is ignored for <see cref="SecurableKind.Database"/> and
    /// <see cref="SecurableKind.Schema"/>, neither of which lives in a schema.
    /// </summary>
    public async Task<IReadOnlyList<SecurableRef>> GetSecurablesAsync(
        SecurableKind kind,
        string? schema,
        CancellationToken ct)
    {
        // Views, matviews, partitioned tables and foreign tables are all TABLE
        // to GRANT, so they are all SecurableKind.Table (see SecurableKind's doc).
        var sql = kind switch
        {
            SecurableKind.Table => $"""
                SELECT c.oid, n.nspname, c.relname
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND (@schema IS NULL OR n.nspname = @schema)
                {HideSystemSchemas}
                ORDER BY n.nspname, c.relname
                """,

            SecurableKind.Sequence => $"""
                SELECT c.oid, n.nspname, c.relname
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind = 'S'
                  AND (@schema IS NULL OR n.nspname = @schema)
                {HideSystemSchemas}
                ORDER BY n.nspname, c.relname
                """,

            SecurableKind.Schema => """
                SELECT n.oid, NULL::text, n.nspname
                FROM pg_catalog.pg_namespace n
                WHERE n.nspname NOT LIKE 'pg\_%'
                  AND n.nspname <> 'information_schema'
                ORDER BY n.nspname
                """,

            SecurableKind.Database => """
                SELECT d.oid, NULL::text, d.datname
                FROM pg_catalog.pg_database d
                WHERE NOT d.datistemplate
                ORDER BY d.datname
                """,

            SecurableKind.Function => $"""
                SELECT p.oid, n.nspname, p.proname,
                       pg_catalog.pg_get_function_arguments(p.oid)
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE (@schema IS NULL OR n.nspname = @schema)
                {HideSystemSchemas}
                ORDER BY n.nspname, p.proname, pg_catalog.pg_get_function_arguments(p.oid)
                """,

            // psql's \dT shape: drop the auto-generated array type of every base
            // type, and drop the row-type Postgres creates behind each table
            // (typrelid <> 0 with a relkind other than 'c'). A standalone
            // composite type keeps its row-type and stays in the list.
            SecurableKind.Type => $"""
                SELECT t.oid, n.nspname, t.typname
                FROM pg_catalog.pg_type t
                JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
                WHERE (t.typrelid = 0
                       OR (SELECT c.relkind = 'c' FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid))
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type el
                                  WHERE el.oid = t.typelem AND el.typarray = t.oid)
                  AND (@schema IS NULL OR n.nspname = @schema)
                {HideSystemSchemas}
                ORDER BY n.nspname, t.typname
                """,

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@schema", StringComparison.Ordinal))
        {
            AddSchemaParameter(command, schema);
        }

        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<SecurableRef>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SecurableRef(
                kind,
                reader.GetFieldValue<uint>(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                kind == SecurableKind.Function && !reader.IsDBNull(3) ? reader.GetString(3) : null));
        }

        return results;
    }

    // ------------------------------------------------------------ object ACL

    /// <summary>
    /// The object's owner and its stored ACL, expanded through
    /// <c>aclexplode()</c>.
    ///
    /// The flag that matters is <see cref="ObjectAcl.IsDefaultAcl"/>: a NULL ACL
    /// column does not mean "nobody has any privileges", it means nobody has
    /// ever run a GRANT or REVOKE here, so the owner holds everything and the
    /// built-in defaults apply. Rendering that as an empty grid is how a
    /// permissions UI teaches the wrong thing, so it is reported as a flag and
    /// left for the caller to render honestly.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The object no longer exists — it was dropped between the picker listing
    /// it and this read.
    /// </exception>
    public async Task<ObjectAcl> GetAclAsync(SecurableRef obj, CancellationToken ct)
    {
        var (aclColumn, ownerColumn) = AclColumns(obj.Kind);
        var catalog = Privileges.CatalogTable(obj.Kind);

        // The three interpolated fragments all come from a switch over the
        // SecurableKind enum, never from user input.
        //
        // LEFT JOIN LATERAL, so an object whose ACL is NULL still yields one row
        // carrying the owner and the is-default flag. The COALESCE to an empty
        // aclitem[] keeps that from depending on aclexplode's strictness.
        var sql = $"""
            SELECT pg_catalog.pg_get_userbyid(t.{ownerColumn}),
                   t.{aclColumn} IS NULL,
                   CASE WHEN a.grantee = 0 THEN NULL
                        ELSE pg_catalog.pg_get_userbyid(a.grantee) END,
                   CASE WHEN a.grantor = 0 THEN NULL
                        ELSE pg_catalog.pg_get_userbyid(a.grantor) END,
                   a.privilege_type,
                   a.is_grantable
            FROM {catalog} t
            LEFT JOIN LATERAL pg_catalog.aclexplode(
                COALESCE(t.{aclColumn}, ARRAY[]::aclitem[])) a ON true
            WHERE t.oid = @oid
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        AddOidParameter(command, obj.Oid);
        await using var reader = await command.ExecuteReaderAsync(ct);

        string? owner = null;
        var isDefault = true;
        var entries = new List<AclEntry>();

        while (await reader.ReadAsync(ct))
        {
            owner ??= reader.GetString(0);
            isDefault = reader.GetBoolean(1);

            if (reader.IsDBNull(4))
            {
                continue; // The LEFT JOIN's placeholder row: no ACL entries at all.
            }

            // An unmodelled privilege from a future server costs its own row,
            // not the whole grid.
            if (Privileges.Parse(reader.GetString(4)) is not { } privilege)
            {
                continue;
            }

            entries.Add(new AclEntry(
                reader.IsDBNull(2) ? null : reader.GetString(2), // grantee 0 == PUBLIC
                reader.IsDBNull(3) ? null : reader.GetString(3),
                privilege,
                reader.GetBoolean(5)));
        }

        if (owner is null)
        {
            throw new InvalidOperationException(
                $"{obj.Display} no longer exists — it was dropped since the object list was built.");
        }

        return new ObjectAcl(obj, owner, isDefault, entries);
    }

    /// <summary>
    /// Per-column grants on one table, from <c>pg_attribute.attacl</c>.
    ///
    /// Only columns that actually carry grants are returned. A NULL
    /// <c>attacl</c> is the normal case and is genuinely nothing: column
    /// privileges are purely additive on top of the table's, so "no column ACL"
    /// means "whatever the table says", not "the owner has everything and the
    /// defaults apply". That is why <see cref="ColumnAcl.IsDefaultAcl"/> is not
    /// the right way to report it and the column is simply omitted — the
    /// opposite call from <see cref="GetAclAsync"/>, for the opposite reason.
    /// </summary>
    public async Task<IReadOnlyList<ColumnAcl>> GetColumnAclsAsync(SecurableRef table, CancellationToken ct)
    {
        // An inner LATERAL join drops the NULL-attacl columns for us.
        const string sql = """
            SELECT a.attname,
                   CASE WHEN e.grantee = 0 THEN NULL
                        ELSE pg_catalog.pg_get_userbyid(e.grantee) END,
                   CASE WHEN e.grantor = 0 THEN NULL
                        ELSE pg_catalog.pg_get_userbyid(e.grantor) END,
                   e.privilege_type,
                   e.is_grantable
            FROM pg_catalog.pg_attribute a
            JOIN LATERAL pg_catalog.aclexplode(a.attacl) e ON true
            WHERE a.attrelid = @oid
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum, e.privilege_type
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        AddOidParameter(command, table.Oid);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var order = new List<string>();
        var byColumn = new Dictionary<string, List<AclEntry>>(StringComparer.Ordinal);

        while (await reader.ReadAsync(ct))
        {
            if (Privileges.Parse(reader.GetString(3)) is not { } privilege)
            {
                continue;
            }

            var column = reader.GetString(0);
            if (!byColumn.TryGetValue(column, out var entries))
            {
                entries = [];
                byColumn[column] = entries;
                order.Add(column);
            }

            entries.Add(new AclEntry(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                privilege,
                reader.GetBoolean(4)));
        }

        return order.Select(c => new ColumnAcl(c, false, byColumn[c])).ToList();
    }

    // --------------------------------------------------------- default ACLs

    /// <summary>
    /// Every <c>ALTER DEFAULT PRIVILEGES</c> in force: what a future object
    /// created by each role, in each schema, will be granted.
    /// <see cref="DefaultPrivilege.Schema"/> is null for the database-wide
    /// default (<c>defaclnamespace = 0</c>).
    /// </summary>
    public async Task<IReadOnlyList<DefaultPrivilege>> GetDefaultPrivilegesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT r.rolname,
                   n.nspname,
                   d.defaclobjtype::text,
                   CASE WHEN e.grantee = 0 THEN NULL
                        ELSE pg_catalog.pg_get_userbyid(e.grantee) END,
                   CASE WHEN e.grantor = 0 THEN NULL
                        ELSE pg_catalog.pg_get_userbyid(e.grantor) END,
                   e.privilege_type,
                   e.is_grantable
            FROM pg_catalog.pg_default_acl d
            JOIN pg_catalog.pg_roles r ON r.oid = d.defaclrole
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid = d.defaclnamespace
            JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) e ON true
            ORDER BY r.rolname, n.nspname NULLS FIRST, d.defaclobjtype, e.privilege_type
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var order = new List<(string Role, string? Schema, SecurableKind Kind)>();
        var grouped = new Dictionary<(string, string?, SecurableKind), List<AclEntry>>();

        while (await reader.ReadAsync(ct))
        {
            if (DefaultAclObjectKind(reader.GetString(2)) is not { } kind)
            {
                continue;
            }

            if (Privileges.Parse(reader.GetString(5)) is not { } privilege)
            {
                continue;
            }

            var key = (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), kind);
            if (!grouped.TryGetValue(key, out var entries))
            {
                entries = [];
                grouped[key] = entries;
                order.Add(key);
            }

            entries.Add(new AclEntry(
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                privilege,
                reader.GetBoolean(6)));
        }

        return order
            .Select(k => new DefaultPrivilege(k.Role, k.Schema, k.Kind, grouped[k]))
            .ToList();
    }

    // ------------------------------------------------------------------ RLS

    /// <summary>
    /// Row-level security state and policies for every table that either has RLS
    /// switched on or has policies at all — a table with policies and RLS off is
    /// inert, which is worth showing rather than hiding
    /// (<see cref="RlsTableState.HasInertPolicies"/>).
    ///
    /// Read from <c>pg_policy</c> rather than the <c>pg_policies</c> view: the
    /// view has no relation OID, so joining it back to <c>pg_class</c> for
    /// <c>relrowsecurity</c>, <c>relforcerowsecurity</c> and the bypass check
    /// would mean matching on schema-plus-table text. <c>polrelid</c> gives that
    /// join for free and keeps all of it in one round trip; the price is calling
    /// <c>pg_get_expr</c> for the quals ourselves, which the view does anyway.
    /// </summary>
    public async Task<IReadOnlyList<RlsTableState>> GetRlsAsync(string? schema, CancellationToken ct)
    {
        // BypassedByCurrentRole is the "works for me, not for the app" footgun,
        // and FORCE ROW LEVEL SECURITY only closes half of it. Postgres treats
        // the two bypass routes differently: a superuser or a BYPASSRLS role
        // skips row security *always*, while the table owner skips it only
        // while the table is not FORCE -- so the FORCE gate wraps the ownership
        // term alone. pg_has_role(..., 'USAGE') rather than a bare relowner
        // comparison because Postgres's own ownership check is
        // has_privs_of_role(), so membership in the owning role bypasses too;
        // rolsuper rides with rolbypassrls because has_bypassrls_privilege() is
        // literally "superuser OR rolbypassrls".
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   (COALESCE((SELECT r.rolsuper OR r.rolbypassrls
                              FROM pg_catalog.pg_roles r
                              WHERE r.rolname = CURRENT_USER), false)
                    OR (NOT c.relforcerowsecurity
                        AND pg_catalog.pg_has_role(CURRENT_USER, c.relowner, 'USAGE'))),
                   p.polname,
                   p.polpermissive,
                   p.polcmd::text,
                   ARRAY(SELECT CASE WHEN ro = 0 THEN 'public'
                                     ELSE pg_catalog.pg_get_userbyid(ro) END
                         FROM unnest(p.polroles) AS ro),
                   pg_catalog.pg_get_expr(p.polqual, p.polrelid),
                   pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_policy p ON p.polrelid = c.oid
            WHERE c.relkind IN ('r', 'p')
              AND (c.relrowsecurity
                   OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy pp WHERE pp.polrelid = c.oid))
              AND (@schema IS NULL OR n.nspname = @schema)
              AND (@schema IS NOT NULL
                   OR (n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'))
            ORDER BY n.nspname, c.relname, p.polname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        AddSchemaParameter(command, schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var order = new List<(string Schema, string Table)>();
        var states = new Dictionary<(string, string), (bool Enabled, bool Force, bool Bypassed, List<RlsPolicyInfo> Policies)>();

        while (await reader.ReadAsync(ct))
        {
            var tableSchema = reader.GetString(0);
            var table = reader.GetString(1);
            var key = (tableSchema, table);

            if (!states.TryGetValue(key, out var state))
            {
                state = (reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), []);
                states[key] = state;
                order.Add(key);
            }

            if (reader.IsDBNull(5))
            {
                continue; // RLS is on but the table has no policies (denies everything).
            }

            state.Policies.Add(new RlsPolicyInfo(
                tableSchema,
                table,
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetFieldValue<string[]>(8),
                PolicyCommand(reader.GetString(7)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return order
            .Select(k =>
            {
                var s = states[k];
                return new RlsTableState(k.Schema, k.Table, s.Enabled, s.Force, s.Bypassed, s.Policies);
            })
            .ToList();
    }

    // -------------------------------------------------------- server answers

    /// <summary>
    /// Asks the server, for every role × privilege pair, whether it is allowed —
    /// in one round trip.
    ///
    /// This is what makes <see cref="EffectivePrivilegeResolver"/> honest.
    /// Unlike a raw ACL read, <c>has_*_privilege()</c> expands role inheritance,
    /// ownership and superuser server-side, using exactly the code path the
    /// executor will use, so it is ground truth: where the resolver's reading of
    /// the ACL disagrees with it, the resolver is wrong and says so.
    ///
    /// The function name is chosen from <see cref="SecurableKind"/> via
    /// <see cref="Privileges.HasPrivilegeFunction"/> — never from user input.
    /// The OID, the role list and the privilege list are all parameters.
    ///
    /// One defensive detail: <c>has_*_privilege()</c> raises
    /// <c>role "x" does not exist</c> for an unknown role name, which would
    /// throw away the whole matrix over one stale entry. The EXISTS filter
    /// against <c>pg_roles</c> drops those pairs instead, and the resolver then
    /// simply trusts its own reading for them. The caller is still responsible
    /// for passing privileges from <see cref="Privileges.For"/> with the real
    /// server version: MAINTAIN against a pre-17 server raises
    /// <c>unrecognized privilege type</c>, which has no equivalent in-SQL guard.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string Role, PrivilegeKind Privilege), bool>> GetServerAnswersAsync(
        SecurableRef obj,
        IReadOnlyList<string> roles,
        IReadOnlyList<PrivilegeKind> privileges,
        CancellationToken ct)
    {
        var answers = new Dictionary<(string Role, PrivilegeKind Privilege), bool>();
        if (roles.Count == 0 || privileges.Count == 0)
        {
            return answers;
        }

        var sql = $"""
            SELECT r.role, p.priv,
                   pg_catalog.{Privileges.HasPrivilegeFunction(obj.Kind)}(r.role::name, @oid, p.priv)
            FROM unnest(@roles::text[]) AS r(role)
            CROSS JOIN unnest(@privs::text[]) AS p(priv)
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_roles ro WHERE ro.rolname = r.role)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        AddOidParameter(command, obj.Oid);
        command.Parameters.Add(new NpgsqlParameter("roles", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = roles.ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("privs", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = privileges.Select(Privileges.Sql).ToArray(),
        });

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Privileges.Parse(reader.GetString(1)) is not { } privilege)
            {
                continue;
            }

            answers[(reader.GetString(0), privilege)] = reader.GetBoolean(2);
        }

        return answers;
    }

    /// <summary>
    /// Whether <paramref name="role"/> has USAGE on <paramref name="schema"/>.
    ///
    /// Its own method because a missing schema USAGE is the single most common
    /// reason a role holding every table privilege still gets
    /// <c>permission denied</c> — the grants are all there and every one of them
    /// is inert. The UI calls it out as its own line rather than burying it in
    /// the matrix.
    ///
    /// Driving the call off <c>pg_roles</c> and <c>pg_namespace</c> keeps
    /// <c>has_schema_privilege</c> from raising "role does not exist" or "schema
    /// does not exist": an unknown role or schema yields no rows, which is
    /// reported as false rather than as an exception.
    /// </summary>
    public async Task<bool> HasSchemaUsageAsync(string role, string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT pg_catalog.has_schema_privilege(r.rolname, n.nspname, 'USAGE')
            FROM pg_catalog.pg_roles r
            CROSS JOIN pg_catalog.pg_namespace n
            WHERE r.rolname = @role AND n.nspname = @schema
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("schema", schema);

        var result = await command.ExecuteScalarAsync(ct);
        return result is bool granted && granted;
    }

    // -------------------------------------------------------------- plumbing

    private static void AddOidParameter(NpgsqlCommand command, uint oid) =>
        command.Parameters.Add(new NpgsqlParameter<uint>("oid", NpgsqlDbType.Oid) { TypedValue = oid });

    private static void AddSchemaParameter(NpgsqlCommand command, string? schema) =>
        command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text)
        {
            Value = (object?)schema ?? DBNull.Value,
        });

    /// <summary>The ACL and owner columns of the catalog an object of this class lives in.</summary>
    private static (string Acl, string Owner) AclColumns(SecurableKind kind) => kind switch
    {
        SecurableKind.Table or SecurableKind.Sequence => ("relacl", "relowner"),
        SecurableKind.Schema => ("nspacl", "nspowner"),
        // pg_database's owner column is datdba, not datowner.
        SecurableKind.Database => ("datacl", "datdba"),
        SecurableKind.Function => ("proacl", "proowner"),
        SecurableKind.Type => ("typacl", "typowner"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>pg_default_acl.defaclobjtype; null for an object class we don't model.</summary>
    private static SecurableKind? DefaultAclObjectKind(string defaclobjtype) => defaclobjtype switch
    {
        "r" => SecurableKind.Table,
        "S" => SecurableKind.Sequence,
        "f" => SecurableKind.Function,
        "T" => SecurableKind.Type,
        "n" => SecurableKind.Schema,
        _ => null,
    };

    /// <summary>pg_policy.polcmd, as GRANT-style keywords.</summary>
    private static string PolicyCommand(string polcmd) => polcmd switch
    {
        "*" => "ALL",
        "r" => "SELECT",
        "a" => "INSERT",
        "w" => "UPDATE",
        "d" => "DELETE",
        _ => polcmd,
    };
}
