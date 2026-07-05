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

    /// <summary>
    /// Executes a parameterized statement that returns no rows (used for
    /// inline cell-edit UPDATEs). Unlike <see cref="ExecuteAsync"/>, callers
    /// supply real parameters instead of a raw user-typed statement.
    /// </summary>
    public async Task ExecuteNonQueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <param name="sql">The statement to execute.</param>
    /// <param name="ct">Cancels the execution mid-flight.</param>
    /// <param name="maxRows">
    /// If set, the result stream ends after this many rows and the query is
    /// cancelled server-side. Merely abandoning the reader doesn't stop
    /// anything: Npgsql's reader disposal drains the entire remaining result
    /// set to leave the connection usable, so a bounded consumer of an
    /// unbounded SELECT would still pull every row over the wire. An explicit
    /// backend cancel makes the drain a no-op.
    /// </param>
    public async Task<StatementResult> ExecuteAsync(string sql, CancellationToken ct, int? maxRows = null)
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
                Batches = StreamBatches(connection, command, reader, maxRows, ct),
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
        int? maxRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var stoppedAtRowCap = false;

        try
        {
            var fieldCount = reader.FieldCount;
            var buffer = new List<object?[]>(BatchSize);
            var produced = 0;

            while (await reader.ReadAsync(ct))
            {
                var row = new object?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                buffer.Add(row);
                produced++;

                if (produced >= maxRows)
                {
                    stoppedAtRowCap = true;
                    if (buffer.Count > 0)
                    {
                        yield return new RowBatch(buffer);
                    }

                    yield break;
                }

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
            // Only cancel when the cap cut the stream short. A cancel request
            // after normal completion could race a pooled-connection reuse and
            // kill an unrelated subsequent query on the same backend.
            if (stoppedAtRowCap)
            {
                command.Cancel();
            }

            try
            {
                await reader.DisposeAsync();
            }
            catch (PostgresException ex) when (stoppedAtRowCap && ex.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                // The backend acknowledged the row-cap cancel while the reader
                // was draining; the rows already yielded are still valid.
            }

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
