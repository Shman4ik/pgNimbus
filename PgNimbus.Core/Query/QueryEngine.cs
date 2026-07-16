using System.Data;
using System.Diagnostics;
using System.Net.Sockets;
using Npgsql;
using Npgsql.PostgresTypes;

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

        // Opening the session connection can itself hit a stale pooled socket
        // (as easily as any other rented connection), so the whole open+BEGIN
        // is retried once on loss, after flushing the pool, same as the
        // non-transaction execution paths below.
        for (var attempt = 0; ; attempt++)
        {
            NpgsqlConnection? connection = null;
            try
            {
                connection = await _dataSource.OpenConnectionAsync(ct);
                await using var command = new NpgsqlCommand("BEGIN", connection);
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex) when (attempt == 0 && IsConnectionLoss(ex))
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }

                ClearPool();
                continue;
            }
            catch
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }

                throw;
            }

            _transactionConnection = connection;
            TransactionStateChanged?.Invoke();
            return;
        }
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
    //
    // connectionLost skips sending the ROLLBACK itself: a dead socket has
    // nothing listening on the other end, and the server already destroyed the
    // transaction on its own the moment it dropped the connection, so sending
    // one would just wait on nothing.
    private async Task AutoRollbackAsync(bool connectionLost = false)
    {
        var connection = _transactionConnection;
        if (connection is null)
        {
            return;
        }

        _transactionConnection = null;
        try
        {
            if (!connectionLost)
            {
                await using var command = new NpgsqlCommand("ROLLBACK", connection);
                await command.ExecuteNonQueryAsync();
            }
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

    // The one message shown for every transaction lost to a dropped
    // connection, wherever it's detected (a statement inside BEGIN…COMMIT, a
    // script running on the transaction's connection). Deliberately doesn't
    // repeat the underlying exception text — "the block is gone, reconnect
    // and start over" is the whole actionable content.
    private static QueryError TransactionLostError(TimeSpan elapsed) => new()
    {
        Elapsed = elapsed,
        Message = "Connection to the server was lost. The open transaction is gone and nothing in it "
            + "was committed. The next statement will reconnect automatically.",
        RolledBack = true,
        ConnectionLost = true,
    };

    // After a detected connection loss the pool may still be holding other
    // dead sockets — every connection that sat idle across the same laptop
    // sleep or tunnel drop — so a single retry could rent another corpse
    // instead of a fresh session. Flushing the whole pool makes the retry
    // deterministic: the next rent is guaranteed to open a new connection.
    private void ClearPool() => _dataSource.Clear();

    // Classifies a failure as "the server-side connection itself is gone" —
    // the dead-socket shape a laptop sleep or a dropped SSH tunnel leaves
    // behind — as opposed to an ordinary statement failure (syntax error,
    // constraint violation, ...) that happened over a perfectly live
    // connection. Only losses are safe to silently retry on a fresh
    // connection; everything else must reach the user as-is.
    private static bool IsConnectionLoss(Exception ex) => ex switch
    {
        OperationCanceledException => false,

        // PostgresException derives from NpgsqlException, so it has to be
        // matched first, or every ordinary server-side error would fall
        // through to the NpgsqlException arm below. Class 08 ("connection
        // exception") plus the two shutdown codes cover both a network-level
        // drop and the server-announced kind (e.g. pg_terminate_backend on a
        // pooled idle connection).
        PostgresException pg => pg.SqlState is { } state &&
            (state.StartsWith("08", StringComparison.Ordinal) ||
             state is PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown),

        // A non-Postgres NpgsqlException wrapping a socket-level failure — the
        // shape a dead pooled connection takes after the peer vanished without
        // a clean close, discovered only once a command tries to use it.
        // TimeoutException is deliberately NOT here: Npgsql also wraps command
        // timeouts (a merely slow query, possibly a write still executing
        // server-side) and pool exhaustion in it, and silently re-running
        // those would double-apply work and pile load onto an already
        // struggling server.
        NpgsqlException { InnerException: IOException or SocketException or EndOfStreamException } => true,

        _ => false,
    };

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
            catch (Exception ex) when (IsConnectionLoss(ex))
            {
                // No live socket to send ROLLBACK down — the server already
                // destroyed the transaction itself when it dropped the
                // connection.
                await AutoRollbackAsync(connectionLost: true);
                throw;
            }
            catch (PostgresException)
            {
                await AutoRollbackAsync();
                throw;
            }

            return;
        }

        // A dead socket from a stale pooled connection almost always fails on
        // send, before the server ever saw the statement, so retrying once on
        // a fresh connection (after flushing the pool) is safe for the common
        // case. The residual risk — the server executed it but the
        // acknowledgement never made it back — is accepted: callers of this
        // method are PK-keyed UPDATE/DELETE grid edits, where a duplicate
        // re-run is a no-op rather than a double-apply.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(ct);
                await using var command = new NpgsqlCommand(sql, connection);

                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }

                await command.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt == 0 && IsConnectionLoss(ex))
            {
                ClearPool();
            }
        }
    }

    /// <summary>
    /// Executes a batch of parameterized statements atomically — safe mode's
    /// "commit everything as one transaction". On its own dedicated connection
    /// the batch runs inside <c>BEGIN…COMMIT</c>; any failure rolls the whole
    /// batch back (nothing is applied) and the exception propagates to the
    /// caller. Inside an explicit user transaction the statements run on the
    /// held session connection instead, joining the open block — atomicity
    /// then comes from that block, and a failure auto-rolls it back like any
    /// other in-transaction statement. Returns the total rows affected.
    /// </summary>
    public async Task<int> ApplyBatchAsync(IReadOnlyList<ParameterizedStatement> statements, CancellationToken ct)
    {
        if (_transactionConnection is { } tx)
        {
            var affected = 0;
            foreach (var statement in statements)
            {
                await using var txCommand = CreateCommand(statement, tx, transaction: null);
                try
                {
                    affected += await txCommand.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex) when (IsConnectionLoss(ex))
                {
                    // No live socket to send ROLLBACK down — the server already
                    // destroyed the transaction itself when it dropped the
                    // connection.
                    await AutoRollbackAsync(connectionLost: true);
                    throw;
                }
                catch (PostgresException)
                {
                    await AutoRollbackAsync();
                    throw;
                }
            }

            return affected;
        }

        // Retried once, whole batch, on a fresh connection after flushing the
        // pool — but only while `committing` is still false. Once CommitAsync
        // has been attempted, a loss no longer means "nothing happened": the
        // commit may have landed server-side before the acknowledgement was
        // lost, so re-running every statement could double-apply. That case
        // isn't retried; it surfaces as-is.
        for (var attempt = 0; ; attempt++)
        {
            NpgsqlConnection? connection = null;
            NpgsqlTransaction? batchTransaction = null;
            var committing = false;
            try
            {
                connection = await _dataSource.OpenConnectionAsync(ct);
                // Disposing an uncommitted NpgsqlTransaction rolls it back, so
                // any failure below undoes every statement already executed.
                batchTransaction = await connection.BeginTransactionAsync(ct);

                var total = 0;
                foreach (var statement in statements)
                {
                    await using var command = CreateCommand(statement, connection, batchTransaction);
                    total += await command.ExecuteNonQueryAsync(ct);
                }

                committing = true;
                await batchTransaction.CommitAsync(ct);
                return total;
            }
            catch (Exception ex) when (attempt == 0 && !committing && IsConnectionLoss(ex))
            {
                ClearPool();
            }
            finally
            {
                if (batchTransaction is not null)
                {
                    try
                    {
                        await batchTransaction.DisposeAsync();
                    }
                    catch
                    {
                        // The connection is exactly as likely to be dead as the
                        // condition this whole method is handling — a rollback
                        // failure here must not replace whichever exception is
                        // already propagating out of the try block above.
                    }
                }

                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }

    private static NpgsqlCommand CreateCommand(ParameterizedStatement statement, NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        var command = new NpgsqlCommand(statement.Sql, connection, transaction);
        foreach (var (name, value) in statement.Parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
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
    /// <param name="allowTextFallback">
    /// Permits re-executing the statement with unreadable columns (unmapped
    /// composites) re-requested in text format. Only safe for statements known
    /// to be side-effect-free — the app-composed browse-mode SELECTs — never
    /// for arbitrary user SQL, where a second execution would apply an
    /// <c>INSERT … RETURNING</c> (or any volatile call) twice.
    /// </param>
    public async Task<StatementResult> ExecuteAsync(string sql, CancellationToken ct, int? maxRows = null, bool allowTextFallback = false)
    {
        // Inside a transaction the statement runs on the shared session
        // connection and its result is fully materialized: a lazily-streaming
        // reader would pin that one connection open, blocking the next statement
        // in the transaction until the grid finished consuming it.
        if (_transactionConnection is not null)
        {
            return await ExecuteInTransactionAsync(sql, maxRows, allowTextFallback, ct);
        }

        var stopwatch = Stopwatch.StartNew();

        // Retried once, on a fresh connection after flushing the pool, but
        // only for the initial phase — open, ExecuteReader, column setup.
        // Once a ResultSet with its streaming Batches enumerable has been
        // returned, rows may already be in the caller's hands; a failure
        // inside StreamBatches itself is a different, untouched code path
        // that never retries.
        for (var attempt = 0; ; attempt++)
        {
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

                // Columns Npgsql can't materialize as objects (unmapped composites
                // and containers of them) are re-requested in text format — one
                // extra round trip, and only for result sets that contain such a
                // column. The re-execution is why callers must opt in: it would
                // run an arbitrary statement's side effects twice.
                if (allowTextFallback && BuildTextFallbackMask(reader) is { } textFallback)
                {
                    await reader.DisposeAsync();
                    command.UnknownResultTypeList = textFallback;
                    reader = await command.ExecuteReaderAsync(CommandBehavior.Default, ct);
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
                try
                {
                    if (reader is not null) await reader.DisposeAsync();
                    if (command is not null) await command.DisposeAsync();
                    if (connection is not null) await connection.DisposeAsync();
                }
                catch
                {
                    // The connection is already faulted (that's exactly the
                    // condition this catch is handling in the loss case); a
                    // cleanup failure here must not replace the exception
                    // that's actually being classified and reported below.
                }

                if (ex is OperationCanceledException)
                {
                    throw;
                }

                var isLoss = IsConnectionLoss(ex);
                if (attempt == 0 && isLoss)
                {
                    ClearPool();
                    continue;
                }

                // A loss can also arrive as a PostgresException (admin/crash
                // shutdown), so the PostgresException arm comes first: it
                // keeps the SqlState/Detail/Hint diagnostics either way and
                // just flags/prefixes the loss instead of flattening it into
                // a detail-free message.
                if (ex is PostgresException pg)
                {
                    return new QueryError
                    {
                        Elapsed = stopwatch.Elapsed,
                        Message = isLoss
                            ? $"Connection to the server was lost and could not be re-established: {pg.MessageText}"
                            : pg.MessageText,
                        SqlState = pg.SqlState,
                        Detail = pg.Detail,
                        Hint = pg.Hint,
                        Position = ParsePosition(pg.Position),
                        ConnectionLost = isLoss,
                    };
                }

                if (isLoss)
                {
                    return new QueryError
                    {
                        Elapsed = stopwatch.Elapsed,
                        Message = $"Connection to the server was lost and could not be re-established: {ex.Message}",
                        ConnectionLost = true,
                    };
                }

                return new QueryError { Elapsed = stopwatch.Elapsed, Message = ex.Message };
            }
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
            for (var i = 0; i < statements.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var statement = statements[i];
                var result = await ExecuteOnConnectionAsync(connection, statement, maxRowsPerStatement, allowTextFallback: false, ct);

                // A connection loss on the very first statement means nothing in
                // the script has run yet — no session state (SET, temp tables)
                // exists to lose — so it's safe to reconnect and retry just that
                // one statement on a fresh connection. Any later statement is
                // left as-is: a quiet mid-script reconnect there would silently
                // drop whatever session state the earlier statements built up,
                // which is worse than surfacing the error.
                if (!inTransaction && i == 0 && result is QueryError { ConnectionLost: true } firstLoss)
                {
                    await connection.DisposeAsync();
                    ClearPool();

                    // The reconnect open can itself fail (server fully down);
                    // that must become a yielded QueryError like every other
                    // script failure, not an exception escaping the enumerable.
                    // yield isn't legal inside a catch, hence the local.
                    QueryError? reconnectFailure = null;
                    try
                    {
                        connection = await _dataSource.OpenConnectionAsync(ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        reconnectFailure = new QueryError
                        {
                            Elapsed = firstLoss.Elapsed,
                            Message = $"Connection to the server was lost and could not be re-established: {ex.Message}",
                            ConnectionLost = true,
                        };
                    }

                    if (reconnectFailure is not null)
                    {
                        yield return reconnectFailure;
                        yield break;
                    }

                    result = await ExecuteOnConnectionAsync(connection, statement, maxRowsPerStatement, allowTextFallback: false, ct);
                }

                if (result is QueryError error)
                {
                    // A failed statement aborts the transaction; undo it and flag
                    // the rollback so the last section can say so. A connection
                    // loss is more final than an ordinary failure — there's no
                    // live socket for ROLLBACK, and the block is gone rather than
                    // recoverable.
                    if (inTransaction)
                    {
                        await AutoRollbackAsync(connectionLost: error.ConnectionLost);
                        yield return error.ConnectionLost ? TransactionLostError(error.Elapsed) : error with { RolledBack = true };
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
    private async Task<StatementResult> ExecuteInTransactionAsync(string sql, int? maxRows, bool allowTextFallback, CancellationToken ct)
    {
        var connection = _transactionConnection!;
        var stopwatch = Stopwatch.StartNew();

        StatementResult result;
        try
        {
            // ExecuteOnConnectionAsync converts PostgresExceptions to QueryError
            // but lets other failures (e.g. a dropped connection) escape; ExecuteAsync
            // promises never to throw those, so translate them here too.
            result = await ExecuteOnConnectionAsync(connection, sql, maxRows, allowTextFallback, ct);
        }
        catch (OperationCanceledException)
        {
            // A cancelled statement doesn't abort the transaction — leave the
            // block open so the user can retry, commit, or roll back explicitly.
            throw;
        }
        catch (Exception ex)
        {
            // A connection loss here has no live socket to send ROLLBACK down —
            // the server already destroyed the transaction itself — so the
            // whole block, not just this statement, is gone. That's a
            // different, more final outcome than an ordinary in-transaction
            // failure (which stays recoverable via the auto-rollback below,
            // since the connection itself is still fine).
            var lost = IsConnectionLoss(ex);
            await AutoRollbackAsync(connectionLost: lost);
            return lost
                ? TransactionLostError(stopwatch.Elapsed)
                : new QueryError { Elapsed = stopwatch.Elapsed, Message = ex.Message, RolledBack = true };
        }

        if (result is QueryError error)
        {
            await AutoRollbackAsync(connectionLost: error.ConnectionLost);
            return error.ConnectionLost
                ? TransactionLostError(error.Elapsed)
                : error with { RolledBack = true };
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
        bool allowTextFallback,
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

            // Same opt-in unmapped-composite text fallback as ExecuteAsync
            // (script statements always pass false — they're arbitrary SQL).
            if (allowTextFallback && BuildTextFallbackMask(reader) is { } textFallback)
            {
                await reader.DisposeAsync();
                command.UnknownResultTypeList = textFallback;
                reader = await command.ExecuteReaderAsync(CommandBehavior.Default, ct);
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
                ConnectionLost = IsConnectionLoss(pg),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A non-Postgres failure (dropped connection, socket timeout, ...):
            // the shared-connection script/transaction paths turn any statement
            // failure into a QueryError rather than letting it escape and crash
            // the app. Cancellation still propagates so it reads as "Cancelled".
            // ConnectionLost is set here — not just left to the caller to
            // re-derive — because it's the one signal ExecuteScriptAsync and
            // ExecuteInTransactionAsync need to tell "the connection is gone"
            // apart from "this statement failed".
            return new QueryError { Elapsed = stopwatch.Elapsed, Message = ex.Message, ConnectionLost = IsConnectionLoss(ex) };
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
                catch
                {
                    // The connection faulted mid-drain; the result we're returning
                    // is already built and the connection is being discarded.
                }
            }
        }
    }

    // Npgsql can't read an unmapped composite type (or an array/domain/range
    // over one) as a plain object — GetValue throws "Reading as 'System.Object'
    // is not supported…". Without a fallback, one composite column makes the
    // whole table unbrowsable. Such columns are re-requested in the text wire
    // format instead, so their values arrive as Postgres literals ("(10,20,cm)")
    // — exactly the shape the grid displays and the composite editor
    // validates and casts back on edit.
    private static bool NeedsTextFormat(PostgresType type) => type switch
    {
        PostgresCompositeType => true,
        PostgresArrayType array => NeedsTextFormat(array.Element),
        PostgresDomainType domain => NeedsTextFormat(domain.BaseType),
        PostgresRangeType range => NeedsTextFormat(range.Subtype),
        PostgresMultirangeType multirange => NeedsTextFormat(multirange.Subrange),
        _ => false,
    };

    /// <summary>
    /// A per-column "request as text" mask for <see cref="NpgsqlCommand.UnknownResultTypeList"/>,
    /// or null when every column materializes fine as-is (the common case — no
    /// re-execution then).
    /// </summary>
    private static bool[]? BuildTextFallbackMask(NpgsqlDataReader reader)
    {
        bool[]? mask = null;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (NeedsTextFormat(reader.GetPostgresType(i)))
            {
                (mask ??= new bool[reader.FieldCount])[i] = true;
            }
        }

        return mask;
    }

    private static IReadOnlyList<ColumnInfo> BuildColumns(NpgsqlDataReader reader)
    {
        // Without CommandBehavior.KeyInfo (readers here always use Default),
        // GetColumnSchema is a pure in-memory read of the wire RowDescription —
        // no catalog round trip. It's the one public API that exposes each
        // column's source-table OID and attribute number, which downstream
        // decide whether a result set maps back onto one editable table.
        var wireSchema = reader.GetColumnSchema();
        var columns = new ColumnInfo[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = new ColumnInfo(
                reader.GetName(i),
                reader.GetDataTypeName(i),
                reader.GetFieldType(i),
                wireSchema[i].TableOID,
                wireSchema[i].ColumnAttributeNumber ?? 0);
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
            catch
            {
                // A faulted reader (e.g. a dropped connection) must not skip the
                // command/connection disposals below, or the pool connection leaks.
            }
            finally
            {
                await command.DisposeAsync();
                await connection.DisposeAsync();
            }
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
