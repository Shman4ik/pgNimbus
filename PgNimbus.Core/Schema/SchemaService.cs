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

public sealed record ColumnDetail(string Name, string DataType, bool NotNull, bool IsPrimaryKey);

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
            SELECT c.relname, c.relkind
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
}
