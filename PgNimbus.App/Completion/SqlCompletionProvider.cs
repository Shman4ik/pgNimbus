using PgNimbus.Core.Schema;

namespace PgNimbus.App.Completion;

/// <summary>
/// Builds the flat candidate list AvaloniaEdit's CompletionWindow filters
/// against: SQL keywords plus every schema/table/column name reachable from
/// the current connection. Rebuilt on demand (e.g. after connecting, or via
/// a manual refresh) rather than tracked incrementally - schemas rarely
/// change mid-session, and a full rebuild is a handful of queries.
/// </summary>
public sealed class SqlCompletionProvider
{
    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET",
        "DELETE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "ON",
        "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "AS", "AND", "OR",
        "NOT", "NULL", "IS", "IN", "EXISTS", "DISTINCT", "UNION", "ALL",
        "CREATE", "TABLE", "ALTER", "DROP", "INDEX", "VIEW", "WITH", "CASE",
        "WHEN", "THEN", "ELSE", "END", "RETURNING", "LIKE", "ILIKE", "BETWEEN",
        "ASC", "DESC", "COUNT", "SUM", "AVG", "MIN", "MAX", "TRUE", "FALSE",
    ];

    private readonly SchemaService _schemaService;
    private IReadOnlyList<SqlCompletionData> _cache = [];

    public SqlCompletionProvider(SchemaService schemaService)
    {
        _schemaService = schemaService;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var items = new List<SqlCompletionData>(Keywords.Select(k => new SqlCompletionData(k, "keyword")));

        var schemas = await _schemaService.GetSchemasAsync(ct);
        foreach (var schema in schemas)
        {
            items.Add(new SqlCompletionData(schema.Name, "schema"));

            var tables = await _schemaService.GetTablesAsync(schema.Name, ct);
            foreach (var table in tables)
            {
                items.Add(new SqlCompletionData(table.Name, $"table ({schema.Name})"));
            }

            var columns = await _schemaService.GetAllColumnsAsync(schema.Name, ct);
            foreach (var column in columns)
            {
                items.Add(new SqlCompletionData(column.Column, $"column ({column.Table})"));
            }
        }

        _cache = items
            .GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public IReadOnlyList<SqlCompletionData> GetCompletionData() => _cache;
}
