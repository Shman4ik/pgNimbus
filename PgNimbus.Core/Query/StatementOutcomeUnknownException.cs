namespace PgNimbus.Core.Query;

/// <summary>
/// The connection was lost after a statement had been sent, so nobody on this
/// side knows whether the server applied it: it may have run to completion with
/// the acknowledgement lost, or a DBA may have terminated it. The engine never
/// sends such a statement again (security audit 2026-09, finding 2), and this
/// type is what lets a caller say so instead of "failed": a user told an INSERT
/// failed presses Add again and inserts the row twice. The streaming paths
/// report the same case as <see cref="QueryError.OutcomeUnknown"/>.
/// </summary>
public sealed class StatementOutcomeUnknownException(string message, Exception innerException)
    : Exception(message, innerException);
