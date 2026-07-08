using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Schema;

/// <summary>Postgres column types offered by the no-SQL "alter table" UI - a fixed allow-list, not free text, since it's concatenated directly into DDL.</summary>
public static class ColumnTypes
{
    public static readonly IReadOnlyList<string> All =
        ["text", "integer", "bigint", "boolean", "numeric", "double precision", "date", "timestamptz", "uuid", "jsonb"];
}

/// <summary>
/// Applies schema changes (ALTER TABLE ADD/DROP/RENAME COLUMN) so the UI can
/// offer no-SQL table editing. Column/table/schema names come from
/// SqlIdentifier.Quote (never string-concatenated raw), and column types are
/// restricted to <see cref="ColumnTypes.All"/> rather than accepting free text.
/// </summary>
public sealed class SchemaEditor
{
    private readonly NpgsqlDataSource _dataSource;

    public SchemaEditor(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public Task AddColumnAsync(string schema, string table, string column, string dataType, bool isNullable, CancellationToken ct)
    {
        if (!ColumnTypes.All.Contains(dataType))
        {
            throw new ArgumentException($"Unsupported column type: {dataType}", nameof(dataType));
        }

        var nullability = isNullable ? "" : " NOT NULL";
        var sql = $"""
            ALTER TABLE {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(table)}
            ADD COLUMN {SqlIdentifier.Quote(column)} {dataType}{nullability}
            """;

        return ExecuteAsync(sql, ct);
    }

    public Task DropColumnAsync(string schema, string table, string column, CancellationToken ct)
    {
        var sql = $"""
            ALTER TABLE {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(table)}
            DROP COLUMN {SqlIdentifier.Quote(column)}
            """;

        return ExecuteAsync(sql, ct);
    }

    public Task RenameColumnAsync(string schema, string table, string oldName, string newName, CancellationToken ct)
    {
        var sql = $"""
            ALTER TABLE {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(table)}
            RENAME COLUMN {SqlIdentifier.Quote(oldName)} TO {SqlIdentifier.Quote(newName)}
            """;

        return ExecuteAsync(sql, ct);
    }

    /// <summary>CREATE EXTENSION for a name taken from pg_available_extensions (quoted, never raw).</summary>
    public Task CreateExtensionAsync(string name, CancellationToken ct) =>
        ExecuteAsync($"CREATE EXTENSION {SqlIdentifier.Quote(name)}", ct);

    public Task DropExtensionAsync(string name, CancellationToken ct) =>
        ExecuteAsync($"DROP EXTENSION {SqlIdentifier.Quote(name)}", ct);

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
