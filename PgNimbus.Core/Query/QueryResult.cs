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
}
