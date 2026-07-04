namespace PgNimbus.Core.Query;

/// <summary>Quotes a Postgres identifier (schema/table/column name) for safe interpolation into SQL text.</summary>
public static class SqlIdentifier
{
    public static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
