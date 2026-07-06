using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Schema;

/// <summary>
/// Reconstructs a <c>CREATE …</c> definition ("source") for a relation straight
/// from pg_catalog. Postgres has no <c>SHOW CREATE TABLE</c>, so this leans on
/// the server-side pretty-printers (<c>pg_get_viewdef</c>,
/// <c>pg_get_constraintdef</c>, <c>pg_get_indexdef</c>, <c>pg_get_expr</c>,
/// <c>format_type</c>) to render real Postgres semantics — identity columns,
/// partition keys, matviews — rather than an approximation.
/// </summary>
public sealed class DdlService
{
    private readonly NpgsqlDataSource _dataSource;

    public DdlService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Builds the DDL for <paramref name="schema"/>.<paramref name="name"/>.
    /// Tables and partitioned tables are reconstructed column-by-column with
    /// their constraints and secondary indexes; views and materialized views use
    /// the server's stored definition.
    /// </summary>
    public async Task<string> GenerateAsync(string schema, string name, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        var (oid, relkind) = await ResolveRelationAsync(connection, schema, name, ct);
        if (oid == 0)
        {
            return $"-- {schema}.{name} not found";
        }

        var qualified = $"{SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(name)}";

        return relkind switch
        {
            'v' => await BuildViewAsync(connection, oid, qualified, materialized: false, ct),
            'm' => await BuildViewAsync(connection, oid, qualified, materialized: true, ct),
            _ => await BuildTableAsync(connection, oid, qualified, partitioned: relkind == 'p', ct),
        };
    }

    private static async Task<(uint Oid, char RelKind)> ResolveRelationAsync(
        NpgsqlConnection connection, string schema, string name, CancellationToken ct)
    {
        const string sql = """
            SELECT c.oid, c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return (0, '\0');
        }

        return (reader.GetFieldValue<uint>(0), reader.GetString(1)[0]);
    }

    private static async Task<string> BuildViewAsync(
        NpgsqlConnection connection, uint oid, string qualified, bool materialized, CancellationToken ct)
    {
        // oid is a numeric value read from pg_class (never user input), so it's
        // inlined directly: Npgsql has no parameter mapping for the oid type.
        await using var command = new NpgsqlCommand($"SELECT pg_get_viewdef({oid}, true)", connection);
        var definition = (await command.ExecuteScalarAsync(ct) as string ?? string.Empty).TrimEnd();

        var keyword = materialized ? "CREATE MATERIALIZED VIEW" : "CREATE VIEW";
        return $"{keyword} {qualified} AS\n{definition}";
    }

    private static async Task<string> BuildTableAsync(
        NpgsqlConnection connection, uint oid, string qualified, bool partitioned, CancellationToken ct)
    {
        var lines = new List<string>();
        lines.AddRange(await ReadColumnsAsync(connection, oid, ct));
        lines.AddRange(await ReadConstraintsAsync(connection, oid, ct));

        var body = string.Join(",\n", lines);
        var partitionClause = partitioned ? await ReadPartitionClauseAsync(connection, oid, ct) : null;
        var suffix = partitionClause is null ? ";" : $"\nPARTITION BY {partitionClause};";

        var ddl = $"CREATE TABLE {qualified} (\n{body}\n){suffix}";

        var indexes = await ReadIndexesAsync(connection, oid, ct);
        if (indexes.Count > 0)
        {
            ddl += "\n\n" + string.Join("\n", indexes.Select(i => i + ";"));
        }

        return ddl;
    }

    private static async Task<List<string>> ReadColumnsAsync(NpgsqlConnection connection, uint oid, CancellationToken ct)
    {
        // oid is a catalog-sourced number (not user input), inlined because
        // Npgsql has no parameter mapping for the oid type.
        var sql = $"""
            SELECT
                a.attname,
                format_type(a.atttypid, a.atttypmod) AS data_type,
                a.attnotnull,
                pg_get_expr(ad.adbin, ad.adrelid) AS default_expr,
                a.attidentity::text
            FROM pg_catalog.pg_attribute a
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
            WHERE a.attrelid = {oid}
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var lines = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            var notNull = reader.GetBoolean(2);
            var defaultExpr = reader.IsDBNull(3) ? null : reader.GetString(3);
            var identity = reader.GetString(4) is { Length: > 0 } s ? s[0] : '\0';

            var line = $"    {SqlIdentifier.Quote(name)} {type}";
            if (identity == 'a')
            {
                line += " GENERATED ALWAYS AS IDENTITY";
            }
            else if (identity == 'd')
            {
                line += " GENERATED BY DEFAULT AS IDENTITY";
            }
            else if (defaultExpr is not null)
            {
                line += $" DEFAULT {defaultExpr}";
            }

            // Identity columns are implicitly NOT NULL; don't restate it.
            if (notNull && identity == '\0')
            {
                line += " NOT NULL";
            }

            lines.Add(line);
        }

        return lines;
    }

    private static async Task<List<string>> ReadConstraintsAsync(NpgsqlConnection connection, uint oid, CancellationToken ct)
    {
        // Primary key first, then unique, then foreign keys, then checks — the
        // conventional reading order. pg_get_constraintdef renders the full body.
        var sql = $"""
            SELECT conname, pg_get_constraintdef(oid, true)
            FROM pg_catalog.pg_constraint
            WHERE conrelid = {oid}
            ORDER BY CASE contype
                         WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'f' THEN 2 ELSE 3
                     END, conname
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var lines = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            lines.Add($"    CONSTRAINT {SqlIdentifier.Quote(reader.GetString(0))} {reader.GetString(1)}");
        }

        return lines;
    }

    private static async Task<List<string>> ReadIndexesAsync(NpgsqlConnection connection, uint oid, CancellationToken ct)
    {
        // Only secondary indexes: skip the primary-key index and any index that
        // merely backs a constraint (already emitted above), so nothing repeats.
        var sql = $"""
            SELECT pg_get_indexdef(i.indexrelid, 0, true)
            FROM pg_catalog.pg_index i
            WHERE i.indrelid = {oid}
              AND NOT i.indisprimary
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_constraint c WHERE c.conindid = i.indexrelid
              )
            ORDER BY i.indexrelid
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var indexes = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    private static async Task<string?> ReadPartitionClauseAsync(NpgsqlConnection connection, uint oid, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT pg_get_partkeydef({oid})", connection);
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
