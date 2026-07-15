using Npgsql;

namespace PgNimbus.Core.Schema;

public sealed record SchemaInfo(string Name);

public enum RelationKind
{
    Table,
    View,
    MaterializedView,
    PartitionedTable,
}

public sealed record TableInfo(string Name, RelationKind Kind);

public sealed record RelationInfo(string Schema, string Name, RelationKind Kind);

public sealed record ColumnDetail(string Name, string DataType, bool NotNull, bool IsPrimaryKey)
{
    /// <summary>
    /// The input affordance the column's values call for (enum dropdown,
    /// checkbox, date picker, …), classified from the column's base type with
    /// domains resolved. <see cref="ColumnValueEditor.Text"/> when nothing
    /// more specific applies.
    /// </summary>
    public ColumnValueEditor Editor { get; init; } = ColumnValueEditor.Text;

    /// <summary>The enum type's labels in declared order when <see cref="Editor"/> is <see cref="ColumnValueEditor.Enum"/>; empty otherwise.</summary>
    public IReadOnlyList<string> EnumLabels { get; init; } = [];

    /// <summary>The base type a domain column resolves to (e.g. "integer" for a domain over integer); null when the declared type isn't a domain.</summary>
    public string? DomainBaseType { get; init; }
}

public sealed record TableColumn(string Table, string Column, string DataType);

/// <summary>A function/procedure/aggregate/window function. <paramref name="Kind"/> is pg_proc.prokind: f, p, a, or w.</summary>
public sealed record FunctionInfo(string Name, string Arguments, string ReturnType, char Kind);

/// <summary>An extension from pg_available_extensions; <paramref name="InstalledVersion"/> is null when not installed.</summary>
public sealed record ExtensionInfo(string Name, string? InstalledVersion, string DefaultVersion, string? Description)
{
    public bool IsInstalled => InstalledVersion is not null;
}

public sealed record RoleInfo(string Name, bool CanLogin, bool IsSuperuser, bool CanCreateDb, bool CanCreateRole);

/// <summary>
/// A foreign key edge: <paramref name="FromColumns"/> on <paramref name="FromSchema"/>.<paramref name="FromTable"/>
/// (the referencing/"child" side) reference <paramref name="ToColumns"/> on
/// <paramref name="ToSchema"/>.<paramref name="ToTable"/> (the referenced/"parent" side), positionally paired.
/// </summary>
public sealed record ForeignKeyInfo(
    string FromSchema, string FromTable, IReadOnlyList<string> FromColumns,
    string ToSchema, string ToTable, IReadOnlyList<string> ToColumns);

/// <summary>
/// Reads structure straight from pg_catalog rather than relying on
/// information_schema, so it reflects the real Postgres model (matviews,
/// partitioned tables, actual type names) instead of the SQL-standard
/// lowest common denominator.
/// </summary>
public sealed class SchemaService
{
    private readonly NpgsqlDataSource _dataSource;

    public SchemaService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT nspname
            FROM pg_catalog.pg_namespace
            WHERE nspname NOT LIKE 'pg\_%'
              AND nspname <> 'information_schema'
            ORDER BY nspname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<SchemaInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SchemaInfo(reader.GetString(0)));
        }

        return results;
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname, c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'v', 'm', 'p')
            ORDER BY c.relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<TableInfo>();
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(1) switch
            {
                "r" => RelationKind.Table,
                "v" => RelationKind.View,
                "m" => RelationKind.MaterializedView,
                "p" => RelationKind.PartitionedTable,
                _ => RelationKind.Table,
            };

            results.Add(new TableInfo(reader.GetString(0), kind));
        }

        return results;
    }

    /// <summary>
    /// Every relation (table/view/matview/partitioned table) across all
    /// non-system schemas in one query, schema-qualified — feeds the command
    /// palette's fuzzy "jump to a table" list without an N+1 per-schema walk.
    /// </summary>
    public async Task<IReadOnlyList<RelationInfo>> GetAllRelationsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname, c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT LIKE 'pg\_%'
              AND n.nspname <> 'information_schema'
              AND c.relkind IN ('r', 'v', 'm', 'p')
            ORDER BY n.nspname, c.relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RelationInfo>();
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(2) switch
            {
                "r" => RelationKind.Table,
                "v" => RelationKind.View,
                "m" => RelationKind.MaterializedView,
                "p" => RelationKind.PartitionedTable,
                _ => RelationKind.Table,
            };

            results.Add(new RelationInfo(reader.GetString(0), reader.GetString(1), kind));
        }

        return results;
    }

    public async Task<IReadOnlyList<ColumnDetail>> GetColumnsAsync(string schema, string table, CancellationToken ct)
    {
        // Besides name/type/nullability/PK, each column carries what its
        // values need for type-aware editing: the base type's pg_type identity
        // (domains walked to their base via typbasetype, so a domain over an
        // enum still classifies as an enum) and, for enums, the pg_enum labels.
        const string sql = """
            WITH RECURSIVE cols AS (
                SELECT
                    a.attnum,
                    a.attname,
                    format_type(a.atttypid, a.atttypmod) AS data_type,
                    a.attnotnull,
                    COALESCE(pk.is_primary_key, false) AS is_primary_key,
                    a.atttypid
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN (
                    SELECT con.conrelid, unnest(con.conkey) AS attnum, true AS is_primary_key
                    FROM pg_catalog.pg_constraint con
                    WHERE con.contype = 'p'
                ) pk ON pk.conrelid = a.attrelid AND pk.attnum = a.attnum
                WHERE n.nspname = @schema
                  AND c.relname = @table
                  AND a.attnum > 0
                  AND NOT a.attisdropped
            ),
            walk AS (
                SELECT c.attnum, c.atttypid AS type_oid, 0 AS depth
                FROM cols c
                UNION ALL
                SELECT w.attnum, t.typbasetype, w.depth + 1
                FROM walk w
                JOIN pg_catalog.pg_type t ON t.oid = w.type_oid
                WHERE t.typbasetype <> 0
            ),
            base AS (
                SELECT DISTINCT ON (attnum) attnum, type_oid
                FROM walk
                ORDER BY attnum, depth DESC
            )
            SELECT
                c.attname,
                c.data_type,
                c.attnotnull,
                c.is_primary_key,
                bt.typname,
                bt.typtype::text,
                bt.typcategory::text,
                CASE WHEN c.atttypid <> bt.oid THEN format_type(bt.oid, NULL) END AS domain_base_type,
                CASE WHEN bt.typtype = 'e' THEN
                    (SELECT array_agg(e.enumlabel ORDER BY e.enumsortorder)
                     FROM pg_catalog.pg_enum e
                     WHERE e.enumtypid = bt.oid)
                END AS enum_labels
            FROM cols c
            JOIN base b ON b.attnum = c.attnum
            JOIN pg_catalog.pg_type bt ON bt.oid = b.type_oid
            ORDER BY c.attnum
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<ColumnDetail>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ColumnDetail(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3))
            {
                Editor = ColumnValueEditorClassifier.Classify(
                    reader.GetString(5)[0],
                    reader.GetString(6)[0],
                    reader.GetString(4)),
                DomainBaseType = reader.IsDBNull(7) ? null : reader.GetString(7),
                EnumLabels = reader.IsDBNull(8) ? [] : reader.GetFieldValue<string[]>(8),
            });
        }

        return results;
    }

    /// <summary>Functions, procedures, aggregates, and window functions in a schema, with identity arguments and result type.</summary>
    public async Task<IReadOnlyList<FunctionInfo>> GetFunctionsAsync(string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT p.proname,
                   pg_catalog.pg_get_function_identity_arguments(p.oid),
                   COALESCE(pg_catalog.pg_get_function_result(p.oid), ''),
                   p.prokind::text
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema
            ORDER BY p.proname, 2
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<FunctionInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new FunctionInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)[0]));
        }

        return results;
    }

    /// <summary>All extensions the server knows, installed first — installed ones carry their version, the rest are available to install.</summary>
    public async Task<IReadOnlyList<ExtensionInfo>> GetExtensionsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT a.name, e.extversion, a.default_version, a.comment
            FROM pg_catalog.pg_available_extensions a
            LEFT JOIN pg_catalog.pg_extension e ON e.extname = a.name
            ORDER BY (e.extversion IS NULL), a.name
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<ExtensionInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ExtensionInfo(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return results;
    }

    /// <summary>Non-system roles with the attribute flags worth showing at a glance.</summary>
    public async Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT rolname, rolcanlogin, rolsuper, rolcreatedb, rolcreaterole
            FROM pg_catalog.pg_roles
            WHERE rolname NOT LIKE 'pg\_%'
            ORDER BY rolname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RoleInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RoleInfo(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return results;
    }

    /// <summary>
    /// Every case-preserved identifier a query might name — schema, relation, and
    /// column names across all user schemas — collected in a single round trip.
    /// Feeds <see cref="Query.IdentifierReconciler"/>, which only needs the set of
    /// real spellings (not their namespaces) to reconcile an unquoted query.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCatalogNamesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT nspname AS name
            FROM pg_catalog.pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
            UNION
            SELECT c.relname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
              AND c.relkind IN ('r', 'v', 'm', 'p')
            UNION
            SELECT a.attname
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
              AND c.relkind IN ('r', 'v', 'm', 'p')
              AND a.attnum > 0 AND NOT a.attisdropped
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// Column names (with their formatted data types) for every table/view in a
    /// schema, in one query - used to power SQL autocomplete without an N+1
    /// GetColumnsAsync call per table.
    /// </summary>
    public async Task<IReadOnlyList<TableColumn>> GetAllColumnsAsync(string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'v', 'm', 'p')
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY c.relname, a.attnum
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<TableColumn>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TableColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return results;
    }

    /// <summary>
    /// Every foreign key across all non-system schemas in one query — the edges
    /// that power completion's "rank JOIN targets by FK" and "offer the join
    /// condition" magic. <c>unnest(conkey, confkey) WITH ORDINALITY</c> zips the
    /// referencing/referenced column-number arrays positionally so a composite
    /// key's columns pair up correctly after the <c>array_agg</c>.
    /// </summary>
    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT ns.nspname, c.relname, array_agg(a.attname ORDER BY k.ord),
                   fns.nspname, fc.relname, array_agg(fa.attname ORDER BY k.ord)
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid = c.relnamespace
            JOIN pg_catalog.pg_class fc ON fc.oid = con.confrelid
            JOIN pg_catalog.pg_namespace fns ON fns.oid = fc.relnamespace
            JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(conkey, confkey, ord) ON true
            JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.conkey
            JOIN pg_catalog.pg_attribute fa ON fa.attrelid = con.confrelid AND fa.attnum = k.confkey
            WHERE con.contype = 'f'
              AND ns.nspname NOT LIKE 'pg\_%' AND ns.nspname <> 'information_schema'
            GROUP BY con.oid, ns.nspname, c.relname, fns.nspname, fc.relname
            ORDER BY ns.nspname, c.relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<ForeignKeyInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ForeignKeyInfo(
                reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<string[]>(5)));
        }

        return results;
    }
}
