namespace PgNimbus.Core.Query;

public sealed record SavedQuery(Guid Id, string Name, string Sql);
