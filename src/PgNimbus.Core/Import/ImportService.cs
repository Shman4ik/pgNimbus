using System.Text;
using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Import;

public sealed record ImportColumn(string Name, string DataType);

/// <summary>
/// Creates the target table (optionally) and bulk-loads parsed rows through
/// COPY ... FROM STDIN (FORMAT csv), so Postgres does the value parsing
/// against the real column types server-side. Identifiers are always quoted;
/// column types are restricted to <see cref="TypeInferrer.Types"/> because
/// they're concatenated into DDL.
/// </summary>
public sealed class ImportService(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    /// <summary>Creates the table (when <paramref name="createTable"/>) and loads every row. Returns the number of rows imported.</summary>
    public async Task<long> ImportAsync(
        string schema,
        string table,
        IReadOnlyList<ImportColumn> columns,
        IReadOnlyList<string?[]> rows,
        bool createTable,
        CancellationToken ct)
    {
        if (columns.Count == 0)
        {
            throw new ArgumentException("No columns to import.", nameof(columns));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        if (createTable)
        {
            foreach (var column in columns)
            {
                if (!TypeInferrer.Types.Contains(column.DataType))
                {
                    throw new ArgumentException($"Unsupported column type: {column.DataType}");
                }
            }

            var definitions = string.Join(",\n    ", columns.Select(c => $"{SqlIdentifier.Quote(c.Name)} {c.DataType}"));
            var createSql = $"CREATE TABLE {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(table)} (\n    {definitions}\n)";
            await using var create = new NpgsqlCommand(createSql, connection);
            await create.ExecuteNonQueryAsync(ct);
        }

        var columnList = string.Join(", ", columns.Select(c => SqlIdentifier.Quote(c.Name)));
        var copySql = $"COPY {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(table)} ({columnList}) FROM STDIN (FORMAT csv)";

        await using (var writer = await connection.BeginTextImportAsync(copySql, ct))
        {
            var line = new StringBuilder();
            foreach (var row in rows)
            {
                line.Clear();
                for (var i = 0; i < columns.Count; i++)
                {
                    if (i > 0)
                    {
                        line.Append(',');
                    }

                    AppendCsvValue(line, i < row.Length ? row[i] : null);
                }

                await writer.WriteLineAsync(line.ToString().AsMemory(), ct);
            }
        }

        return rows.Count;
    }

    /// <summary>COPY csv conventions: null → empty unquoted, everything else quoted with "" escaping (so an empty string stays an empty string).</summary>
    private static void AppendCsvValue(StringBuilder line, string? value)
    {
        if (value is null)
        {
            return;
        }

        line.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }
}
