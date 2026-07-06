using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Completion;

/// <summary>
/// Supplies the candidate list AvaloniaEdit's CompletionWindow shows — SQL
/// keywords plus every schema/table/column reachable from the current
/// connection — but shapes it to the caret instead of always handing back the
/// same flat dump:
/// <list type="bullet">
/// <item><b>Member access</b> — after <c>alias.</c>/<c>table.</c>, only that
/// table's columns (or, after <c>schema.</c>, that schema's tables).</item>
/// <item><b>Bare identifier</b> — everything, but with the columns of the
/// tables named in the statement's FROM clause floated to the top and
/// pre-selected, so the query you're actually writing ranks first. System
/// schemas (<c>pg_catalog</c> et al.) are already excluded upstream by
/// <see cref="SchemaService"/>, so no noise to filter here.</item>
/// </list>
/// The catalog is read once (on connect / manual refresh) into the structured
/// caches below; per-keystroke work is just a regex read of the editor text and
/// a few dictionary lookups.
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

    // Ranking bands. Current-table columns sit far above the rest so a match
    // among them wins pre-selection; the base bands only order ties within the
    // catalog-wide list.
    private const double CurrentColumnPriority = 100;
    private const double TablePriority = 10;
    private const double ColumnPriority = 5;
    private const double SchemaPriority = 1;

    private readonly SchemaService _schemaService;

    // Structured snapshot of the catalog, rebuilt by RefreshAsync.
    private IReadOnlyList<(string Schema, string Table)> _tables = [];
    // Columns keyed by both the bare table name and "schema.table", so an alias
    // resolved to either spelling finds them. Case-insensitive.
    private Dictionary<string, List<string>> _columnsByTable = new(StringComparer.OrdinalIgnoreCase);
    // The catalog-wide candidate list (keywords + schemas + tables + all
    // columns), deduped and pre-built so bare-identifier completion only has to
    // prepend the current table's columns.
    private IReadOnlyList<SqlCompletionData> _baseItems = [];

    public SqlCompletionProvider(SchemaService schemaService)
    {
        _schemaService = schemaService;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var tables = new List<(string Schema, string Table)>();
        var columnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var baseItems = new List<SqlCompletionData>(Keywords.Select(k => new SqlCompletionData(k, "keyword")));

        var schemas = await _schemaService.GetSchemasAsync(ct);
        foreach (var schema in schemas)
        {
            // The bare name filters the list; the quote-if-needed form is what gets
            // inserted, so accepting a mixed-case object writes "Spells" not spells.
            baseItems.Add(new SqlCompletionData(schema.Name, "schema", SqlIdentifier.QuoteIfNeeded(schema.Name), SchemaPriority));

            var schemaTables = await _schemaService.GetTablesAsync(schema.Name, ct);
            foreach (var table in schemaTables)
            {
                tables.Add((schema.Name, table.Name));
                baseItems.Add(new SqlCompletionData(table.Name, $"table ({schema.Name})", SqlIdentifier.QuoteIfNeeded(table.Name), TablePriority));
            }

            var columns = await _schemaService.GetAllColumnsAsync(schema.Name, ct);
            foreach (var column in columns)
            {
                Index(columnsByTable, column.Table, column.Column);
                Index(columnsByTable, $"{schema.Name}.{column.Table}", column.Column);
                baseItems.Add(new SqlCompletionData(column.Column, $"column ({column.Table})", SqlIdentifier.QuoteIfNeeded(column.Column), ColumnPriority));
            }
        }

        _tables = tables;
        _columnsByTable = columnsByTable;
        _baseItems = Dedupe(baseItems);
    }

    /// <summary>
    /// The candidates to show for the caret at <paramref name="caretOffset"/> in
    /// <paramref name="sql"/> — member-access columns/tables after a
    /// <c>qualifier.</c>, otherwise the full catalog with the current FROM
    /// tables' columns floated to the top.
    /// </summary>
    public IReadOnlyList<SqlCompletionData> GetCompletionData(string sql, int caretOffset)
    {
        var qualifier = SqlCompletionContext.GetQualifierBeforeCaret(sql, caretOffset);
        return qualifier is not null
            ? GetMemberCompletions(qualifier, sql)
            : GetGeneralCompletions(sql);
    }

    // After "qualifier.": the columns of the table the qualifier names (directly
    // or via a FROM-clause alias), or — when the qualifier is a schema — that
    // schema's tables. Empty when nothing resolves (so no stray list pops up).
    private IReadOnlyList<SqlCompletionData> GetMemberCompletions(string qualifier, string sql)
    {
        foreach (var table in SqlCompletionContext.ExtractTables(sql))
        {
            if (table.Alias is not null && string.Equals(table.Alias, qualifier, StringComparison.OrdinalIgnoreCase)
                && ColumnsFor(table.Schema, table.Table) is { } aliased)
            {
                return ColumnItems(aliased, table.Table);
            }
        }

        if (ColumnsFor(schema: "", qualifier) is { } direct)
        {
            return ColumnItems(direct, qualifier);
        }

        // schema. → the schema's tables
        return _tables
            .Where(t => string.Equals(t.Schema, qualifier, StringComparison.OrdinalIgnoreCase))
            .Select(t => new SqlCompletionData(t.Table, $"table ({t.Schema})", SqlIdentifier.QuoteIfNeeded(t.Table), TablePriority))
            .ToList();
    }

    // Bare identifier: the whole catalog, with the columns of the FROM tables
    // hoisted to the front (and top priority) so the statement's own columns win.
    private IReadOnlyList<SqlCompletionData> GetGeneralCompletions(string sql)
    {
        var items = new List<SqlCompletionData>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in SqlCompletionContext.ExtractTables(sql))
        {
            if (!added.Add($"{table.Schema}.{table.Table}") || ColumnsFor(table.Schema, table.Table) is not { } columns)
            {
                continue;
            }

            foreach (var column in columns)
            {
                items.Add(new SqlCompletionData(column, $"column ({table.Table})", SqlIdentifier.QuoteIfNeeded(column), CurrentColumnPriority));
            }
        }

        items.AddRange(_baseItems);
        return Dedupe(items);
    }

    private IReadOnlyList<string>? ColumnsFor(string schema, string table)
    {
        if (!string.IsNullOrEmpty(schema) && _columnsByTable.TryGetValue($"{schema}.{table}", out var qualified))
        {
            return qualified;
        }

        return _columnsByTable.TryGetValue(table, out var bare) ? bare : null;
    }

    private static List<SqlCompletionData> ColumnItems(IReadOnlyList<string> columns, string table) =>
        columns.Select(c => new SqlCompletionData(c, $"column ({table})", SqlIdentifier.QuoteIfNeeded(c), CurrentColumnPriority)).ToList();

    private static void Index(Dictionary<string, List<string>> map, string table, string column)
    {
        if (!map.TryGetValue(table, out var columns))
        {
            map[table] = columns = [];
        }

        // Distinct per table (the same column can surface twice when a bare name
        // collides across schemas), order-preserving.
        if (!columns.Contains(column, StringComparer.Ordinal))
        {
            columns.Add(column);
        }
    }

    // Collapse duplicate labels, keeping the first — which, because callers
    // prepend the higher-priority items, is the better-ranked one.
    private static IReadOnlyList<SqlCompletionData> Dedupe(IEnumerable<SqlCompletionData> items) =>
        items.GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
}
