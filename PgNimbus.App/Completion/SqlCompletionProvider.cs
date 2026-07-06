using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Completion;

/// <summary>
/// Supplies the candidate list AvaloniaEdit's CompletionWindow shows — SQL
/// keywords, common functions, plus every schema/table/column reachable from
/// the current connection — but shapes it to the caret instead of always
/// handing back the same flat dump:
/// <list type="bullet">
/// <item><b>Inside a string literal or comment</b> — nothing; no popup while
/// typing prose.</item>
/// <item><b>Member access</b> — after <c>alias.</c>/<c>table.</c>, only that
/// table's columns (or, after <c>schema.</c>, that schema's tables).</item>
/// <item><b>Table position</b> — after FROM/JOIN/INTO/UPDATE, only what can
/// legally go there: tables, schemas, the statement's CTE names, and keywords —
/// no column noise.</item>
/// <item><b>Anywhere else</b> — everything, but with the columns of the tables
/// the statement touches (FROM/JOIN plus UPDATE/INSERT INTO targets) floated to
/// the top and pre-selected, and the statement's aliases offered too. System
/// schemas (<c>pg_catalog</c> et al.) are already excluded upstream by
/// <see cref="SchemaService"/>, so no noise to filter here.</item>
/// </list>
/// The catalog is read once (on connect / manual refresh) into the structured
/// caches below; per-keystroke work is one scan of the editor text and a few
/// dictionary lookups.
/// </summary>
public sealed class SqlCompletionProvider
{
    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET",
        "DELETE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS",
        "NATURAL", "LATERAL", "ON", "USING", "GROUP", "BY", "ORDER", "HAVING",
        "LIMIT", "OFFSET", "AS", "AND", "OR", "NOT", "NULL", "IS", "IN",
        "EXISTS", "DISTINCT", "UNION", "INTERSECT", "EXCEPT", "ALL", "ANY",
        "CREATE", "TABLE", "ALTER", "DROP", "INDEX", "VIEW", "WITH", "RECURSIVE",
        "MATERIALIZED", "CASE", "WHEN", "THEN", "ELSE", "END", "RETURNING",
        "LIKE", "ILIKE", "BETWEEN", "ASC", "DESC", "TRUE", "FALSE", "CAST",
        "INTERVAL", "ARRAY", "CONFLICT", "DO", "NOTHING", "DEFAULT", "PRIMARY",
        "KEY", "REFERENCES", "CASCADE", "IF",
    ];

    // Everyday Postgres functions, curated rather than read from pg_proc — the
    // full catalog is thousands of overloads of noise. Inserted as "name()"
    // with the caret placed between the parens (see SqlCompletionData.Complete).
    private static readonly string[] Functions =
    [
        // aggregates & window
        "count", "sum", "avg", "min", "max", "array_agg", "string_agg",
        "json_agg", "jsonb_agg", "bool_and", "bool_or",
        "row_number", "rank", "dense_rank", "ntile", "lag", "lead",
        "first_value", "last_value",
        // conditional
        "coalesce", "nullif", "greatest", "least",
        // strings
        "lower", "upper", "initcap", "length", "trim", "ltrim", "rtrim",
        "lpad", "rpad", "replace", "substring", "split_part", "position",
        "strpos", "concat", "concat_ws", "format", "reverse",
        "regexp_replace", "starts_with",
        // numeric
        "abs", "round", "ceil", "floor", "trunc", "power", "sqrt", "mod",
        "random",
        // date/time
        "now", "age", "date_trunc", "date_part", "extract", "to_char",
        "to_date", "to_timestamp", "make_date", "justify_interval",
        // arrays / sets / json
        "unnest", "generate_series", "array_length", "cardinality",
        "array_to_string", "string_to_array", "to_json", "to_jsonb",
        "jsonb_build_object", "jsonb_array_elements",
        // misc
        "md5", "gen_random_uuid", "pg_typeof",
    ];

    // Ranking bands. Current-statement items (its tables' columns, its aliases)
    // sit far above the rest so a match among them wins pre-selection; the base
    // bands only order ties within the catalog-wide list.
    private const double CurrentColumnPriority = 100;
    private const double AliasPriority = 90;
    private const double CtePriority = 20;
    private const double TablePriority = 10;
    private const double ColumnPriority = 5;
    private const double FunctionPriority = 3;
    private const double SchemaPriority = 1;

    private readonly SchemaService _schemaService;

    // Structured snapshot of the catalog, rebuilt by RefreshAsync.
    private IReadOnlyList<(string Schema, string Table)> _tables = [];
    // Columns keyed by both the bare table name and "schema.table", so an alias
    // resolved to either spelling finds them. Case-insensitive.
    private Dictionary<string, List<TableColumn>> _columnsByTable = new(StringComparer.OrdinalIgnoreCase);
    // The catalog-wide candidate list (keywords + functions + schemas + tables +
    // all columns), deduped and pre-built so bare-identifier completion only has
    // to prepend the current statement's items.
    private IReadOnlyList<SqlCompletionData> _baseItems = [];
    // Its table-position subset: keywords + schemas + tables, no columns/functions.
    private IReadOnlyList<SqlCompletionData> _tableRefItems = [];

    public SqlCompletionProvider(SchemaService schemaService)
    {
        _schemaService = schemaService;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var tables = new List<(string Schema, string Table)>();
        var columnsByTable = new Dictionary<string, List<TableColumn>>(StringComparer.OrdinalIgnoreCase);

        var keywordItems = Keywords.Select(k => new SqlCompletionData(k, "keyword")).ToList();
        var baseItems = new List<SqlCompletionData>(keywordItems);
        baseItems.AddRange(Functions.Select(f => new SqlCompletionData(f, "function", $"{f}()", FunctionPriority)));
        // Keywords go *after* the catalog here: with nothing typed yet the list
        // renders in insertion order, and right after FROM/JOIN the point is the
        // tables, not SELECT/WHERE.
        var tableRefItems = new List<SqlCompletionData>();

        var schemas = await _schemaService.GetSchemasAsync(ct);
        foreach (var schema in schemas)
        {
            // The bare name filters the list; the quote-if-needed form is what gets
            // inserted, so accepting a mixed-case object writes "Spells" not spells.
            var schemaItem = new SqlCompletionData(schema.Name, "schema", SqlIdentifier.QuoteIfNeeded(schema.Name), SchemaPriority);
            baseItems.Add(schemaItem);
            tableRefItems.Add(schemaItem);

            var schemaTables = await _schemaService.GetTablesAsync(schema.Name, ct);
            foreach (var table in schemaTables)
            {
                tables.Add((schema.Name, table.Name));
                var tableItem = new SqlCompletionData(table.Name, $"table ({schema.Name})", SqlIdentifier.QuoteIfNeeded(table.Name), TablePriority);
                baseItems.Add(tableItem);
                tableRefItems.Add(tableItem);
            }

            var columns = await _schemaService.GetAllColumnsAsync(schema.Name, ct);
            foreach (var column in columns)
            {
                Index(columnsByTable, column.Table, column);
                Index(columnsByTable, $"{schema.Name}.{column.Table}", column);
                baseItems.Add(ColumnItem(column, ColumnPriority));
            }
        }

        tableRefItems.AddRange(keywordItems);

        _tables = tables;
        _columnsByTable = columnsByTable;
        _baseItems = Dedupe(baseItems);
        _tableRefItems = Dedupe(tableRefItems);
    }

    /// <summary>
    /// The candidates to show for the caret at <paramref name="caretOffset"/> in
    /// <paramref name="sql"/> — nothing inside strings/comments, member-access
    /// columns/tables after a <c>qualifier.</c>, tables in table position,
    /// otherwise the full catalog with the current statement's columns floated
    /// to the top.
    /// </summary>
    public IReadOnlyList<SqlCompletionData> GetCompletionData(string sql, int caretOffset)
    {
        var context = SqlCompletionContext.GetCaretContext(sql, caretOffset);
        if (context.InStringOrComment)
        {
            return [];
        }

        var qualifier = SqlCompletionContext.GetQualifierBeforeCaret(sql, caretOffset);
        if (qualifier is not null)
        {
            return GetMemberCompletions(qualifier, sql);
        }

        return context.Clause == SqlClause.TableRef
            ? GetTableRefCompletions(sql)
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
                return ColumnItems(aliased);
            }
        }

        if (ColumnsFor(schema: "", qualifier) is { } direct)
        {
            return ColumnItems(direct);
        }

        // schema. → the schema's tables
        return _tables
            .Where(t => string.Equals(t.Schema, qualifier, StringComparison.OrdinalIgnoreCase))
            .Select(t => new SqlCompletionData(t.Table, $"table ({t.Schema})", SqlIdentifier.QuoteIfNeeded(t.Table), TablePriority))
            .ToList();
    }

    // Table position (after FROM/JOIN/INTO/UPDATE …): only what can be a table
    // there — the statement's CTEs first, then schemas + tables (+ keywords, so
    // "JOIN"/"WHERE" still complete after "FROM users "). No columns.
    private IReadOnlyList<SqlCompletionData> GetTableRefCompletions(string sql)
    {
        var items = new List<SqlCompletionData>();
        foreach (var cte in SqlCompletionContext.ExtractCteNames(sql))
        {
            items.Add(new SqlCompletionData(cte, "CTE", SqlIdentifier.QuoteIfNeeded(cte), CtePriority));
        }

        items.AddRange(_tableRefItems);
        return Dedupe(items);
    }

    // Bare identifier: the whole catalog, with the columns of the statement's
    // tables hoisted to the front (and top priority) so the statement's own
    // columns win, plus its aliases and CTE names.
    private IReadOnlyList<SqlCompletionData> GetGeneralCompletions(string sql)
    {
        var items = new List<SqlCompletionData>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in SqlCompletionContext.ExtractTables(sql))
        {
            if (table.Alias is not null)
            {
                items.Add(new SqlCompletionData(table.Alias, $"alias ({table.Table})", table.Alias, AliasPriority));
            }

            if (!added.Add($"{table.Schema}.{table.Table}") || ColumnsFor(table.Schema, table.Table) is not { } columns)
            {
                continue;
            }

            foreach (var column in columns)
            {
                items.Add(ColumnItem(column, CurrentColumnPriority));
            }
        }

        foreach (var cte in SqlCompletionContext.ExtractCteNames(sql))
        {
            items.Add(new SqlCompletionData(cte, "CTE", SqlIdentifier.QuoteIfNeeded(cte), CtePriority));
        }

        items.AddRange(_baseItems);
        return Dedupe(items);
    }

    private IReadOnlyList<TableColumn>? ColumnsFor(string schema, string table)
    {
        if (!string.IsNullOrEmpty(schema) && _columnsByTable.TryGetValue($"{schema}.{table}", out var qualified))
        {
            return qualified;
        }

        return _columnsByTable.TryGetValue(table, out var bare) ? bare : null;
    }

    private static List<SqlCompletionData> ColumnItems(IReadOnlyList<TableColumn> columns) =>
        columns.Select(c => ColumnItem(c, CurrentColumnPriority)).ToList();

    private static SqlCompletionData ColumnItem(TableColumn column, double priority) =>
        new(column.Column, $"column ({column.Table}) : {column.DataType}", SqlIdentifier.QuoteIfNeeded(column.Column), priority);

    private static void Index(Dictionary<string, List<TableColumn>> map, string key, TableColumn column)
    {
        if (!map.TryGetValue(key, out var columns))
        {
            map[key] = columns = [];
        }

        // Distinct per table (the same column can surface twice when a bare name
        // collides across schemas), order-preserving.
        if (!columns.Any(c => string.Equals(c.Column, column.Column, StringComparison.Ordinal)))
        {
            columns.Add(column);
        }
    }

    // Collapse duplicate labels, keeping the first — which, because callers
    // prepend the higher-priority items, is the better-ranked one.
    private static IReadOnlyList<SqlCompletionData> Dedupe(IEnumerable<SqlCompletionData> items) =>
        items.GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
}
