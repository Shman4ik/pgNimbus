namespace PgNimbus.Core.Query;

/// <summary>
/// A single past execution, kept for quick recall - not for audit purposes.
/// <paramref name="Pinned"/> entries float to the top of the history list and
/// survive both the store's size cap and "Clear history".
/// <paramref name="Connection"/> is a display label ("host/database") for
/// per-connection scoping; null on entries recorded before it existed.
/// <paramref name="Redacted"/> is <see cref="QueryHistoryStore"/>'s record that
/// this entry's text and result line have been through the redactor (see
/// <see cref="QueryHistoryStore.Redact"/>); nothing else sets or reads it.
/// </summary>
public sealed record QueryHistoryEntry(
    string Sql,
    DateTimeOffset ExecutedAt,
    double ElapsedMs,
    string Summary,
    bool Pinned = false,
    string? Connection = null,
    string? Redacted = null);
