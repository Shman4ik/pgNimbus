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

public sealed record ColumnDetail(string Name, string DataType, bool NotNull, bool IsPrimaryKey);

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
        const string sql = """
            SELECT
                a.attname,
                format_type(a.atttypid, a.atttypmod) AS data_type,
                a.attnotnull,
                COALESCE(pk.is_primary_key, false) AS is_primary_key
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
            ORDER BY a.attnum
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
                reader.GetBoolean(3)));
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
}
