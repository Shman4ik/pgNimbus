namespace PgNimbus.Core.Query;

public sealed record ColumnInfo(string Name, string DataTypeName, Type ClrType);

public sealed record RowBatch(IReadOnlyList<object?[]> Rows);

/// <summary>
/// Outcome of executing a single SQL statement. One of <see cref="ResultSet"/>,
/// <see cref="CommandResult"/>, or <see cref="QueryError"/>.
/// </summary>
public abstract record StatementResult
{
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// A result-returning statement (SELECT, RETURNING, etc). Rows stream in
/// batches so the UI can render the first screenful before the query finishes.
/// </summary>
public sealed record ResultSet : StatementResult
{
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public required IAsyncEnumerable<RowBatch> Batches { get; init; }
}

/// <summary>
/// A fully-materialized result-returning statement. Used by script execution,
/// where several statements share one connection and each result must be read to
/// completion before the next statement can run — so, unlike <see cref="ResultSet"/>,
/// the rows can't stream out lazily and are collected up front instead.
/// </summary>
public sealed record MaterializedResultSet : StatementResult
{
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>True when the row cap cut this statement's result short.</summary>
    public bool Truncated { get; init; }
}

/// <summary>A non-result statement (INSERT/UPDATE/DDL/etc).</summary>
public sealed record CommandResult : StatementResult
{
    public required long RowsAffected { get; init; }
    public required string CommandTag { get; init; }
}

/// <summary>A failed statement, carrying enough detail to highlight the offending SQL.</summary>
public sealed record QueryError : StatementResult
{
    public required string Message { get; init; }
    public string? SqlState { get; init; }
    public string? Detail { get; init; }
    public string? Hint { get; init; }

    /// <summary>1-based character position into the submitted SQL, for editor error highlighting.</summary>
    public int? Position { get; init; }

    /// <summary>
    /// True when this failure happened inside an explicit transaction and the
    /// engine auto-rolled it back — the block is gone, so the UI can say so.
    /// </summary>
    public bool RolledBack { get; init; }

    /// <summary>
    /// True when this failure is the server-side connection itself going away
    /// (dead socket after a laptop sleep, a dropped SSH tunnel, or the server
    /// terminating the backend) rather than an ordinary statement failure. The
    /// engine already retried once on a fresh connection before surfacing
    /// this — a second loss means the caller should treat the connection (and,
    /// if one was open, the transaction on it) as gone rather than retry again.
    /// </summary>
    public bool ConnectionLost { get; init; }
}
