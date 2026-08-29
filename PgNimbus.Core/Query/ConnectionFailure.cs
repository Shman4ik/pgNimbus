using System.Net.Sockets;
using Npgsql;

namespace PgNimbus.Core.Query;

/// <summary>
/// Tells "the connection itself is gone" apart from "the statement failed over
/// a perfectly live connection". Only the first is safe to recover from by
/// silently reaching for a fresh connection; everything else has to reach the
/// user as-is.
///
/// Lives here rather than inside <see cref="QueryEngine"/> because it is not
/// only the query path that has to answer the question: the LISTEN/NOTIFY
/// listener holds a connection open for hours and has to decide, when its wait
/// loop throws, whether to re-establish it or to report the channel dead.
/// One classifier so the two never drift into disagreeing about what a dropped
/// socket looks like.
/// </summary>
public static class ConnectionFailure
{
    /// <summary>
    /// True when <paramref name="ex"/> is the dead-socket shape a laptop sleep,
    /// a dropped SSH tunnel or a <c>pg_terminate_backend</c> leaves behind.
    /// </summary>
    public static bool IsLoss(Exception ex) => ex switch
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
}
