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

    // Non-null while an explicit user transaction is open. Every execution then
    // runs on this one connection (instead of a fresh pooled one) so BEGIN and a
    // later COMMIT/ROLLBACK bracket the same session. Cleared — and the
    // connection disposed back to the pool — when the transaction ends.
    private NpgsqlConnection? _transactionConnection;

    public QueryEngine(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>True while an explicit BEGIN…COMMIT/ROLLBACK transaction is open.</summary>
    public bool IsInTransaction => _transactionConnection is not null;

    /// <summary>
    /// Raised whenever the explicit-transaction state changes — a BEGIN, a
    /// COMMIT/ROLLBACK, or an auto-rollback after a statement failed. Lets the UI
    /// keep its "in transaction" indicator in sync no matter which path changed
    /// the state. May fire on a background thread, so subscribers that touch UI
    /// must marshal to their UI thread.
    /// </summary>
    public event Action? TransactionStateChanged;

    /// <summary>
    /// Opens an explicit transaction: takes a dedicated connection and runs
    /// <c>BEGIN</c> on it. Subsequent executions run on that connection until
    /// <see cref="CommitAsync"/> or <see cref="RollbackAsync"/>. A no-op if a
    /// transaction is already open.
    /// </summary>
    public async Task BeginTransactionAsync(CancellationToken ct)
    {
        if (_transactionConnection is not null)
        {
            return;
        }

        var connection = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("BEGIN", connection);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        _transactionConnection = connection;
        TransactionStateChanged?.Invoke();
    }

    /// <summary>Commits the open transaction. A no-op if none is open.</summary>
    public Task CommitAsync(CancellationToken ct) => EndTransactionAsync("COMMIT", ct);

    /// <summary>Rolls back the open transaction. A no-op if none is open.</summary>
    public Task RollbackAsync(CancellationToken ct) => EndTransactionAsync("ROLLBACK", ct);

    // Ends the transaction with COMMIT or ROLLBACK. The session connection is
    // always released and the state cleared (in the finally), even when the verb
    // itself fails — a failed COMMIT still ends the transaction server-side, so
    // leaving the connection marked "in transaction" would be a lie.
    private async Task EndTransactionAsync(string verb, CancellationToken ct)
    {
        var connection = _transactionConnection;
        if (connection is null)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand(verb, connection);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _transactionConnection = null;
            await connection.DisposeAsync();
            TransactionStateChanged?.Invoke();
        }
    }

    // Auto-rollback after a statement failed inside a transaction. A failed
    // statement puts Postgres in the "current transaction is aborted" state where
    // every further statement errors until the block is rolled back, so rather
    // than strand the user there, undo the whole transaction and release the
    // connection. Best-effort: a rollback that itself fails still clears state.
    private async Task AutoRollbackAsync()
    {
        var connection = _transactionConnection;
        if (connection is null)
        {
            return;
        }

        _transactionConnection = null;
        try
        {
            await using var command = new NpgsqlCommand("ROLLBACK", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // The connection is being discarded regardless; a rollback failure
            // here changes nothing the caller can act on.
        }
        finally
        {
            await connection.DisposeAsync();
            TransactionStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Executes a parameterized statement that returns no rows (used for
    /// inline cell-edit UPDATEs). Unlike <see cref="ExecuteAsync"/>, callers
    /// supply real parameters instead of a raw user-typed statement.
    /// </summary>
    public async Task ExecuteNonQueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        // Inside a transaction the edit must run on the session connection so it's
        // part of the same block; a failure there aborts the transaction, so
        // auto-rollback before surfacing the error.
        if (_transactionConnection is { } tx)
        {
            await using var txCommand = new NpgsqlCommand(sql, tx);
            foreach (var (name, value) in parameters)
            {
                txCommand.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            try
            {
                await txCommand.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException)
            {
                await AutoRollbackAsync();
                throw;
            }

            return;
        }

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
        // Inside a transaction the statement runs on the shared session
        // connection and its result is fully materialized: a lazily-streaming
        // reader would pin that one connection open, blocking the next statement
        // in the transaction until the grid finished consuming it.
        if (_transactionConnection is not null)
        {
            return await ExecuteInTransactionAsync(sql, maxRows, ct);
        }

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

    /// <summary>
    /// Runs a multi-statement script and yields one <see cref="StatementResult"/>
    /// per statement, in order. All statements execute on a single connection, so
    /// session state carries across them (a <c>BEGIN…COMMIT</c> block, <c>SET</c>,
    /// temp tables), which sequential <see cref="ExecuteAsync"/> calls — each on
    /// its own pooled connection — cannot. Each result carries its own elapsed
    /// time (the per-statement timing). Execution stops at the first
    /// <see cref="QueryError"/> (like psql's <c>ON_ERROR_STOP</c>): the error is
    /// yielded and no further statements run, since the transaction is aborted.
    /// </summary>
    /// <param name="statements">The already-split statements to run in order.</param>
    /// <param name="maxRowsPerStatement">
    /// If set, each result-returning statement keeps at most this many rows;
    /// hitting the cap flags the result truncated and cancels that statement
    /// server-side (see <see cref="ExecuteAsync"/> for why abandoning the reader
    /// isn't enough).
    /// </param>
    /// <param name="ct">Cancels the script mid-flight, between or within statements.</param>
    public async IAsyncEnumerable<StatementResult> ExecuteScriptAsync(
        IReadOnlyList<string> statements,
        int? maxRowsPerStatement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // In a transaction the whole script runs on the shared session connection
        // (so it joins the open block); otherwise it gets its own pooled
        // connection that's disposed when the script ends.
        var inTransaction = _transactionConnection is not null;
        var connection = inTransaction ? _transactionConnection! : await _dataSource.OpenConnectionAsync(ct);

        try
        {
            foreach (var statement in statements)
            {
                ct.ThrowIfCancellationRequested();

                var result = await ExecuteOnConnectionAsync(connection, statement, maxRowsPerStatement, ct);

                if (result is QueryError error)
                {
                    // A failed statement aborts the transaction; undo it and flag
                    // the rollback so the last section can say so.
                    if (inTransaction)
                    {
                        await AutoRollbackAsync();
                        yield return error with { RolledBack = true };
                    }
                    else
                    {
                        yield return error;
                    }

                    yield break;
                }

                yield return result;
            }
        }
        finally
        {
            // AutoRollbackAsync already disposed the session connection on the
            // error path; only a pooled (non-transaction) connection is ours to
            // release here.
            if (!inTransaction)
            {
                await connection.DisposeAsync();
            }
        }
    }

    // Runs one statement on the open transaction's connection and materializes
    // its result (see ExecuteAsync for why streaming is avoided here). A failure
    // auto-rolls-back the transaction and comes back flagged so the UI can note
    // that the block is gone.
    private async Task<StatementResult> ExecuteInTransactionAsync(string sql, int? maxRows, CancellationToken ct)
    {
        var connection = _transactionConnection!;
        var stopwatch = Stopwatch.StartNew();

        StatementResult result;
        try
        {
            // ExecuteOnConnectionAsync converts PostgresExceptions to QueryError
            // but lets other failures (e.g. a dropped connection) escape; ExecuteAsync
            // promises never to throw those, so translate them here too.
            result = await ExecuteOnConnectionAsync(connection, sql, maxRows, ct);
        }
        catch (OperationCanceledException)
        {
            // A cancelled statement doesn't abort the transaction — leave the
            // block open so the user can retry, commit, or roll back explicitly.
            throw;
        }
        catch (Exception ex)
        {
            await AutoRollbackAsync();
            return new QueryError { Elapsed = stopwatch.Elapsed, Message = ex.Message, RolledBack = true };
        }

        if (result is QueryError error)
        {
            await AutoRollbackAsync();
            return error with { RolledBack = true };
        }

        return result;
    }

    // Executes one statement on an already-open connection and materializes its
    // result. Separate from ExecuteAsync because the shared-connection script
    // path can't hand off a lazily-streaming reader: the next statement can't run
    // until this one's reader is fully read and closed.
    private static async Task<StatementResult> ExecuteOnConnectionAsync(
        NpgsqlConnection connection,
        string sql,
        int? maxRows,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var command = new NpgsqlCommand(sql, connection);

        NpgsqlDataReader? reader = null;
        var truncated = false;
        try
        {
            reader = await command.ExecuteReaderAsync(CommandBehavior.Default, ct);

            if (reader.FieldCount == 0)
            {
                var rowsAffected = reader.RecordsAffected;
                return new CommandResult
                {
                    Elapsed = stopwatch.Elapsed,
                    RowsAffected = rowsAffected < 0 ? 0 : rowsAffected,
                    CommandTag = BuildCommandTag(sql),
                };
            }

            var columns = BuildColumns(reader);
            var fieldCount = reader.FieldCount;
            var rows = new List<object?[]>();

            while (await reader.ReadAsync(ct))
            {
                if (maxRows is { } cap && rows.Count >= cap)
                {
                    // A row past the cap exists: keep exactly `cap` rows, flag the
                    // truncation, and cancel so disposal doesn't drain the rest.
                    truncated = true;
                    command.Cancel();
                    break;
                }

                var row = new object?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[i] = value is DBNull ? null : value;
                }

                rows.Add(row);
            }

            return new MaterializedResultSet
            {
                Elapsed = stopwatch.Elapsed,
                Columns = columns,
                Rows = rows,
                Truncated = truncated,
            };
        }
        catch (PostgresException pg)
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
        finally
        {
            if (reader is not null)
            {
                try
                {
                    await reader.DisposeAsync();
                }
                catch (PostgresException ex) when (truncated && ex.SqlState == PostgresErrorCodes.QueryCanceled)
                {
                    // The backend acknowledged the row-cap cancel while draining;
                    // the rows already collected are still valid.
                }
            }
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
                    // Non-sequential access: the row is fully buffered once
                    // ReadAsync returns, so sync GetValue never blocks on I/O.
                    // One call per cell instead of IsDBNullAsync + GetValue -
                    // half a million awaits per 100k×5 result was measurable.
                    var value = reader.GetValue(i);
                    row[i] = value is DBNull ? null : value;
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
