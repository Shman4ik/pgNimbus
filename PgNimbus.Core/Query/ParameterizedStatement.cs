namespace PgNimbus.Core.Query;

/// <summary>A single SQL statement plus the parameter values it executes with.</summary>
public sealed record ParameterizedStatement(string Sql, IReadOnlyDictionary<string, object?> Parameters);
