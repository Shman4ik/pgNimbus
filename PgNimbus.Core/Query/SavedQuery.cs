namespace PgNimbus.Core.Query;

/// <summary>
/// One entry in the user's Saved Queries list. <paramref name="UpdatedAt"/>
/// records the last time it was written, which is what makes re-saving an
/// already-saved tab an <em>overwrite</em> visible to the user rather than a
/// silent no-op: a saved-queries.json written before this field existed
/// deserializes with it defaulting to <see langword="default"/>.
/// </summary>
public sealed record SavedQuery(Guid Id, string Name, string Sql, DateTimeOffset UpdatedAt = default);
