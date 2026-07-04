using System.Data;
using System.Diagnostics;
using Npgsql;

namespace PgNimbus.Core.Query;

/// <summary>
/// Executes SQL against a Postgres server. Result rows stream out in small
/// batches so the caller can render the first screenful before the whole
/// result set has arrived, and every execution can be cancelled mid-flight.
/// </summary>
public sealed class QueryEngine
{
    private const int BatchSize = 200;

    private readonly NpgsqlDataSource _dataSource;

    public QueryEngine(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<StatementResult> ExecuteAsync(string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        NpgsqlConnection? connection = null;
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;

        try
        {
            connection = await _dataSource.OpenConnectionAsync(ct);
            command = new NpgsqlCommand(sql, connection);
            reader = await command.ExecuteReaderAsync(CommandBehavior.Default, ct);

            if (reader.FieldCount == 0)
            {
                var rowsAffected = reader.RecordsAffected;
                var tag = BuildCommandTag(sql);

                await reader.DisposeAsync();
                await command.DisposeAsync();
                await connection.DisposeAsync();

                return new CommandResult
                {
                    Elapsed = stopwatch.Elapsed,
                    RowsAffected = rowsAffected < 0 ? 0 : rowsAffected,
                    CommandTag = tag,
                };
            }

            var columns = BuildColumns(reader);

            // Ownership of connection/command/reader passes to the streaming
            // enumerable below; it disposes them once enumeration ends.
            return new ResultSet
            {
                Elapsed = stopwatch.Elapsed,
                Columns = columns,
                Batches = StreamBatches(connection, command, reader, ct),
            };
        }
        catch (Exception ex)
        {
            if (reader is not null) await reader.DisposeAsync();
            if (command is not null) await command.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();

            if (ex is OperationCanceledException)
            {
                throw;
            }

            if (ex is PostgresException pg)
            {
                return new QueryError
                {
                    Elapsed = stopwatch.Elapsed,
                    Message = pg.MessageText,
                    SqlState = pg.SqlState,
                    Detail = pg.Detail,
                    Hint = pg.Hint,
                    Position = ParsePosition(pg.Position),
                };
            }

            return new QueryError { Elapsed = stopwatch.Elapsed, Message = ex.Message };
        }
    }

    private static IReadOnlyList<ColumnInfo> BuildColumns(NpgsqlDataReader reader)
    {
        var columns = new ColumnInfo[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = new ColumnInfo(reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i));
        }

        return columns;
    }

    private static async IAsyncEnumerable<RowBatch> StreamBatches(
        NpgsqlConnection connection,
        NpgsqlCommand command,
        NpgsqlDataReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            var fieldCount = reader.FieldCount;
            var buffer = new List<object?[]>(BatchSize);

            while (await reader.ReadAsync(ct))
            {
                var row = new object?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                buffer.Add(row);

                if (buffer.Count >= BatchSize)
                {
                    yield return new RowBatch(buffer);
                    buffer = new List<object?[]>(BatchSize);
                }
            }

            if (buffer.Count > 0)
            {
                yield return new RowBatch(buffer);
            }
        }
        finally
        {
            await reader.DisposeAsync();
            await command.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static string BuildCommandTag(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();
        var end = trimmed.IndexOfAny(" \t\r\n".AsSpan());
        var keyword = end < 0 ? trimmed : trimmed[..end];
        return keyword.IsEmpty ? "OK" : keyword.ToString().ToUpperInvariant();
    }

    private static int? ParsePosition(int position) => position > 0 ? position : null;
}
