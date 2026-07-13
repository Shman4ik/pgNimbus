using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

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
/// no column noise. A table completes schema-qualified (<c>public.users</c>) so
/// the reference resolves regardless of the search_path.</item>
/// <item><b>Predicate position</b> — after WHERE/ON/HAVING/GROUP BY/ORDER BY,
/// only the columns of the tables the statement pulls FROM (plus its aliases,
/// CTEs, functions and keywords); the rest of the catalog's columns and tables
/// are dropped, since nothing else is in scope for a row predicate.</item>
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
    private const double JoinConditionPriority = 200;
    private const double CurrentColumnPriority = 100;
    private const double AliasPriority = 90;
    private const double FkTablePriority = 15;
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
    // Its predicate subset: keywords + functions only, no catalog columns/tables —
    // a WHERE/ON/BY caret gets these plus just the FROM tables' own columns.
    private IReadOnlyList<SqlCompletionData> _predicateBaseItems = [];
    // FK edges across every schema; the graph-matching (which tables are FK-adjacent,
    // what condition connects two of them) is pure Core logic in ForeignKeyMatcher —
    // this is just its input, refreshed alongside everything else below.
    private IReadOnlyList<ForeignKeyInfo> _foreignKeys = [];

    public SqlCompletionProvider(SchemaService schemaService)
    {
        _schemaService = schemaService;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var tables = new List<(string Schema, string Table)>();
        var columnsByTable = new Dictionary<string, List<TableColumn>>(StringComparer.OrdinalIgnoreCase);

        var keywordItems = Keywords.Select(k => new SqlCompletionData(k, SqlCompletionKind.Keyword)).ToList();
        var functionItems = Functions.Select(f => new SqlCompletionData(f, SqlCompletionKind.Function, $"{f}()", FunctionPriority)).ToList();
        var baseItems = new List<SqlCompletionData>(keywordItems);
        baseItems.AddRange(functionItems);
        // Keywords go *after* the catalog here: with nothing typed yet the list
        // renders in insertion order, and right after FROM/JOIN the point is the
        // tables, not SELECT/WHERE.
        var tableRefItems = new List<SqlCompletionData>();

        var schemas = await _schemaService.GetSchemasAsync(ct);
        foreach (var schema in schemas)
        {
            // The bare name filters the list; the quote-if-needed form is what gets
            // inserted, so accepting a mixed-case object writes "Spells" not spells.
            var schemaItem = new SqlCompletionData(schema.Name, SqlCompletionKind.Schema, SqlIdentifier.QuoteIfNeeded(schema.Name), SchemaPriority);
            baseItems.Add(schemaItem);
            tableRefItems.Add(schemaItem);

            var schemaTables = await _schemaService.GetTablesAsync(schema.Name, ct);
            foreach (var table in schemaTables)
            {
                tables.Add((schema.Name, table.Name));
                // Elsewhere a table completes to its bare name; in table position
                // (after FROM/JOIN) it completes schema-qualified ("public.users")
                // so the reference is unambiguous whatever the search_path is.
                var qualified = $"{SqlIdentifier.QuoteIfNeeded(schema.Name)}.{SqlIdentifier.QuoteIfNeeded(table.Name)}";
                baseItems.Add(new SqlCompletionData(table.Name, SqlCompletionKind.Table, SqlIdentifier.QuoteIfNeeded(table.Name), TablePriority) { AliasTable = table.Name, Detail = schema.Name });
                tableRefItems.Add(new SqlCompletionData(table.Name, SqlCompletionKind.Table, qualified, TablePriority) { AliasTable = table.Name, Detail = schema.Name });
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

        var foreignKeys = await _schemaService.GetForeignKeysAsync(ct);

        _tables = tables;
        _columnsByTable = columnsByTable;
        _baseItems = Dedupe(baseItems);
        _tableRefItems = Dedupe(tableRefItems);
        // A predicate caret gets keywords + functions but no catalog columns/tables;
        // GetPredicateCompletions prepends just the FROM tables' own columns.
        _predicateBaseItems = Dedupe(keywordItems.Concat(functionItems));
        _foreignKeys = foreignKeys;
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

        return context.Clause switch
        {
            SqlClause.TableRef or SqlClause.FromTableRef => GetTableRefCompletions(sql),
            SqlClause.JoinTableRef when SqlCompletionContext.IsAfterCompleteJoinTarget(sql, caretOffset) => GetJoinKeywordCompletions(sql),
            SqlClause.JoinTableRef => GetJoinTableRefCompletions(sql),
            SqlClause.Predicate when SqlCompletionContext.IsAfterOnKeyword(sql, caretOffset) => GetJoinConditionCompletions(sql),
            SqlClause.Predicate => GetPredicateCompletions(sql),
            _ => GetGeneralCompletions(sql),
        };
    }

    // After "qualifier.": the columns of the CTE or table the qualifier names
    // (directly or via a FROM-clause alias), or — when the qualifier is a
    // schema — that schema's tables. A CTE shadows a same-named catalog table,
    // matching how Postgres resolves the reference. Empty when nothing
    // resolves (so no stray list pops up).
    private IReadOnlyList<SqlCompletionData> GetMemberCompletions(string qualifier, string sql)
    {
        var ctes = SqlCompletionContext.ExtractCteDefinitions(sql);

        foreach (var table in SqlCompletionContext.ExtractTables(sql))
        {
            if (table.Alias is null || !string.Equals(table.Alias, qualifier, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (table.Schema.Length == 0 && CteColumnItems(table.Table, ctes) is { Count: > 0 } viaAlias)
            {
                return viaAlias;
            }

            if (ColumnsFor(table.Schema, table.Table) is { } aliased)
            {
                return ColumnItems(aliased);
            }
        }

        if (CteColumnItems(qualifier, ctes) is { Count: > 0 } cteColumns)
        {
            return cteColumns;
        }

        if (ColumnsFor(schema: "", qualifier) is { } direct)
        {
            return ColumnItems(direct);
        }

        // schema. → the schema's tables
        return _tables
            .Where(t => string.Equals(t.Schema, qualifier, StringComparison.OrdinalIgnoreCase))
            .Select(t => new SqlCompletionData(t.Table, SqlCompletionKind.Table, SqlIdentifier.QuoteIfNeeded(t.Table), TablePriority) { AliasTable = t.Table, Detail = t.Schema })
            .ToList();
    }

    // Table position (after FROM/INTO/UPDATE …): only what can be a table there —
    // the statement's CTEs first, then schemas + tables (+ keywords, so
    // "JOIN"/"WHERE" still complete after "FROM users "). No columns.
    private IReadOnlyList<SqlCompletionData> GetTableRefCompletions(string sql) =>
        BuildTableRefCompletions(sql, boosted: []);

    // Table position specifically after JOIN: same as GetTableRefCompletions, but
    // tables connected by a foreign key to one already in the statement are
    // floated above the flat catalog dump — the "flagship" JOIN magic.
    private IReadOnlyList<SqlCompletionData> GetJoinTableRefCompletions(string sql) =>
        BuildTableRefCompletions(sql, FkNeighborItems(sql));

    // Table position after JOIN, but the table+alias is already fully typed
    // (trailing whitespace) — ON/USING are the only grammatical next tokens, so
    // they're boosted to the top instead of the flat table/keyword dump.
    private IReadOnlyList<SqlCompletionData> GetJoinKeywordCompletions(string sql) =>
        BuildTableRefCompletions(sql, JoinKeywordBoostItems);

    private static readonly IReadOnlyList<SqlCompletionData> JoinKeywordBoostItems =
    [
        new SqlCompletionData("ON", SqlCompletionKind.Keyword, "ON", JoinConditionPriority),
        new SqlCompletionData("USING", SqlCompletionKind.Keyword, "USING", JoinConditionPriority),
    ];

    private IReadOnlyList<SqlCompletionData> BuildTableRefCompletions(string sql, IEnumerable<SqlCompletionData> boosted)
    {
        var items = new List<SqlCompletionData>();
        foreach (var cte in SqlCompletionContext.ExtractCteNames(sql))
        {
            items.Add(new SqlCompletionData(cte, SqlCompletionKind.Cte, SqlIdentifier.QuoteIfNeeded(cte), CtePriority));
        }

        items.AddRange(boosted);
        items.AddRange(_tableRefItems);
        return Dedupe(items);
    }

    // Every table FK-adjacent to a table already in the statement (either side of
    // the relationship — the new table can be the "many" or the "one" side),
    // excluding tables the statement already references. The graph walk itself is
    // pure Core logic (ForeignKeyMatcher, unit-tested there); this just maps the
    // App's parsed TableRef to Core's TableReference and wraps the result.
    private List<SqlCompletionData> FkNeighborItems(string sql)
    {
        var statementTables = ToTableReferences(SqlCompletionContext.ExtractTables(sql));
        var items = new List<SqlCompletionData>();
        foreach (var (neighborSchema, neighborTable) in ForeignKeyMatcher.FindJoinCandidates(statementTables, _foreignKeys))
        {
            var qualified = $"{SqlIdentifier.QuoteIfNeeded(neighborSchema)}.{SqlIdentifier.QuoteIfNeeded(neighborTable)}";
            items.Add(new SqlCompletionData(neighborTable, SqlCompletionKind.Table, qualified, FkTablePriority)
            {
                AliasTable = neighborTable,
                Detail = neighborSchema,
                DescriptionText = "table · FK match",
            });
        }

        return items;
    }

    // The join condition suggestion after ON: pairs the most recently joined
    // table with the closest earlier table it has a direct FK to, and offers
    // "child.fk_col = parent.pk_col" (AND-joined for a composite key) as the
    // single top item — one keystroke (Enter) completes the whole condition.
    private IReadOnlyList<SqlCompletionData> GetJoinConditionCompletions(string sql)
    {
        var predicateItems = GetPredicateCompletions(sql);
        var statementTables = ToTableReferences(SqlCompletionContext.ExtractTables(sql));
        if (ForeignKeyMatcher.BuildJoinCondition(statementTables, _foreignKeys) is not { } condition)
        {
            return predicateItems;
        }

        var joinItem = new SqlCompletionData(condition, SqlCompletionKind.JoinCondition, condition, JoinConditionPriority);
        var items = new List<SqlCompletionData>(predicateItems.Count + 1) { joinItem };
        items.AddRange(predicateItems);
        return Dedupe(items);
    }

    private static List<TableReference> ToTableReferences(IReadOnlyList<SqlCompletionContext.TableRef> tables) =>
        tables.Select(t => new TableReference(t.Schema, t.Table, t.Alias)).ToList();

    // Bare identifier: the whole catalog, with the columns of the statement's
    // tables hoisted to the front (and top priority) so the statement's own
    // columns win, plus its aliases and CTE names.
    private IReadOnlyList<SqlCompletionData> GetGeneralCompletions(string sql)
    {
        var items = CollectStatementItems(sql, out _);
        items.AddRange(_baseItems);
        return Dedupe(items);
    }

    // Predicate/row position (WHERE, ON, HAVING, GROUP/ORDER BY, USING): only the
    // columns of the FROM tables can be named here, so we offer those (plus the
    // statement's aliases, CTE names, functions and keywords) and drop the rest of
    // the catalog's columns/tables/schemas. If nothing in FROM resolves to known
    // columns yet, fall back to the full catalog rather than a near-empty list.
    private IReadOnlyList<SqlCompletionData> GetPredicateCompletions(string sql)
    {
        var items = CollectStatementItems(sql, out var resolvedColumns);
        if (!resolvedColumns)
        {
            items.AddRange(_baseItems);
            return Dedupe(items);
        }

        items.AddRange(_predicateBaseItems);
        return Dedupe(items);
    }

    // The current statement's own contributions — every FROM/UPDATE/INTO table's
    // columns (top priority) and aliases, plus CTE names. A FROM table that is
    // really one of the statement's CTEs contributes the CTE's derived output
    // columns instead of (shadowed) catalog ones. Sets
    // <paramref name="resolvedColumns"/> when at least one table resolved to real
    // columns, so a predicate caret knows whether it can safely narrow.
    private List<SqlCompletionData> CollectStatementItems(string sql, out bool resolvedColumns)
    {
        var items = new List<SqlCompletionData>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ctes = SqlCompletionContext.ExtractCteDefinitions(sql);
        resolvedColumns = false;

        foreach (var table in SqlCompletionContext.ExtractTables(sql))
        {
            if (table.Alias is not null)
            {
                items.Add(new SqlCompletionData(table.Alias, SqlCompletionKind.Alias, table.Alias, AliasPriority)
                {
                    Detail = table.Table,
                    DescriptionText = $"alias for {table.Table}",
                });
            }

            if (!added.Add($"{table.Schema}.{table.Table}"))
            {
                continue;
            }

            if (table.Schema.Length == 0 && CteColumnItems(table.Table, ctes) is { Count: > 0 } cteColumns)
            {
                resolvedColumns = true;
                items.AddRange(cteColumns);
                continue;
            }

            if (ColumnsFor(table.Schema, table.Table) is not { } columns)
            {
                continue;
            }

            resolvedColumns = true;
            foreach (var column in columns)
            {
                items.Add(ColumnItem(column, CurrentColumnPriority));
            }
        }

        foreach (var cte in ctes)
        {
            items.Add(new SqlCompletionData(cte.Name, SqlCompletionKind.Cte, SqlIdentifier.QuoteIfNeeded(cte.Name), CtePriority));
        }

        return items;
    }

    /// <summary>
    /// Expands the <c>*</c> / <c>alias.*</c> in the select list of the
    /// statement under the caret into explicit columns — CTEs resolve through
    /// their derived output columns, everything else through the catalog.
    /// Null when there's no star to expand or a table can't be resolved.
    /// </summary>
    public SqlCompletionContext.StarExpansion? ExpandSelectStar(string sql, int caret)
    {
        var ctes = SqlCompletionContext.ExtractCteDefinitions(sql);
        return SqlCompletionContext.ExpandSelectStar(sql, caret, (schema, table) =>
        {
            if (schema.Length == 0 && CteColumnItems(table, ctes) is { Count: > 0 } cteColumns)
            {
                return cteColumns.Select(c => c.Text).ToList();
            }

            return ColumnsFor(schema, table)?.Select(c => c.Column).ToList();
        });
    }

    // The columns `name` exposes when it names one of the statement's CTEs:
    // the derived output columns, plus — when the CTE SELECTs * — the columns
    // of its source tables, each itself a CTE (recursively; a self-reference
    // in a RECURSIVE body is cut by the visited set) or a catalog table. Null
    // when `name` names no CTE at all.
    private List<SqlCompletionData>? CteColumnItems(string name, IReadOnlyList<SqlCompletionContext.CteDefinition> ctes)
    {
        var items = new List<SqlCompletionData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return AddCteColumns(name, ctes, items, seen, visited) ? items : null;
    }

    private bool AddCteColumns(
        string name,
        IReadOnlyList<SqlCompletionContext.CteDefinition> ctes,
        List<SqlCompletionData> items,
        HashSet<string> seen,
        HashSet<string> visited)
    {
        if (!visited.Add(name))
        {
            return false;
        }

        SqlCompletionContext.CteDefinition? found = null;
        foreach (var candidate in ctes)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                found = candidate;
                break;
            }
        }

        if (found is not { } cte)
        {
            return false;
        }

        foreach (var column in cte.Columns)
        {
            if (seen.Add(column))
            {
                items.Add(new SqlCompletionData(column, SqlCompletionKind.Column, SqlIdentifier.QuoteIfNeeded(column), CurrentColumnPriority)
                {
                    DescriptionText = $"column · {cte.Name}",
                });
            }
        }

        if (!cte.SelectsStar)
        {
            return true;
        }

        foreach (var source in cte.SourceTables)
        {
            if (source.Schema.Length == 0 && AddCteColumns(source.Table, ctes, items, seen, visited))
            {
                continue;
            }

            if (ColumnsFor(source.Schema, source.Table) is not { } columns)
            {
                continue;
            }

            foreach (var column in columns)
            {
                if (seen.Add(column.Column))
                {
                    items.Add(ColumnItem(column, CurrentColumnPriority));
                }
            }
        }

        return true;
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

    // The data type rides in Detail (right-aligned in the row); the tooltip
    // names the owning table, which the row itself doesn't show.
    private static SqlCompletionData ColumnItem(TableColumn column, double priority) =>
        new(column.Column, SqlCompletionKind.Column, SqlIdentifier.QuoteIfNeeded(column.Column), priority)
        {
            Detail = column.DataType,
            DescriptionText = $"column · {column.Table}",
        };

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
