namespace PgNimbus.Core.Query;

/// <summary>A single past execution, kept for quick recall - not for audit purposes.</summary>
public sealed record QueryHistoryEntry(string Sql, DateTimeOffset ExecutedAt, double ElapsedMs, string Summary);
