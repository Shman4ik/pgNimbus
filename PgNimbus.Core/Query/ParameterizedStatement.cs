namespace PgNimbus.Core.Query;

/// <summary>
/// A single SQL statement plus the parameter values it executes with.
/// <see cref="ExpectedRowsAffected"/>, when set, is a promise the batch checks:
/// a key-targeted UPDATE/DELETE that touches anything other than exactly that
/// many rows aborts the whole batch (see <see cref="QueryEngine.ApplyBatchAsync(IReadOnlyList{ParameterizedStatement}, StagedRowCheck?, CancellationToken)"/>).
/// </summary>
public sealed record ParameterizedStatement(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    int? ExpectedRowsAffected = null);
