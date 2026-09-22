using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

namespace PgNimbus.App.Completion;

/// <summary>A relation the completion catalog knows, with its columns in ordinal order.</summary>
public sealed record CompletionTable(string Schema, string Name, IReadOnlyList<TableColumn> Columns);

/// <summary>A catalog function, procedure or aggregate, with the schema that owns it.</summary>
public sealed record CompletionFunction(string Schema, FunctionInfo Function);

/// <summary>
/// Whether completion's catalog is current. <paramref name="IsStale"/> after a
/// refresh failed: the previous catalog is still what completion offers, and
/// <paramref name="Error"/> says why it could not be replaced.
/// </summary>
public sealed record CompletionCatalogStatus(bool IsStale, string? Error);

/// <summary>
/// Everything completion knows about the database, read in one refresh and
/// swapped in as one value. <paramref name="SearchPath"/> is the order short
/// names resolve in; null when it couldn't be read, and then a short name
/// resolves only when exactly one schema has it.
/// </summary>
public sealed record CompletionCatalog(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<CompletionTable> Tables,
    IReadOnlyList<CompletionFunction> Functions,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<string>? SearchPath)
{
    /// <summary>pg_catalog's functions: never offered as candidates (thousands of internal overloads), only read for argument hints.</summary>
    public IReadOnlyList<CompletionFunction> BuiltinFunctions { get; init; } = [];

    /// <summary>The types a cast can name (see <see cref="SchemaService.GetTypesAsync"/>); empty until read, when a short built-in list stands in.</summary>
    public IReadOnlyList<DataTypeInfo> Types { get; init; } = [];
}

/// <summary>
/// Supplies the candidate list AvaloniaEdit's CompletionWindow shows — SQL
/// keywords, common functions, plus every schema/table/column reachable from
/// the current connection — but shapes it to the caret instead of always
/// handing back the same flat dump:
/// <list type="bullet">
/// <item><b>Only the statement under the caret counts.</b> Everything is read
/// from the text between the real <c>;</c> tokens around it (the part right of
/// the caret included, so a FROM typed after the select list still names the
/// list's sources) — a neighbouring statement's tables never leak in.</item>
/// <item><b>Only the block under the caret counts, and what it can see.</b>
/// Within the statement, the SELECT/DML block the caret is in decides what can
/// be named (see <see cref="SqlScopeModel"/>): an <c>EXISTS (…)</c>'s tables
/// never leak into the outer WHERE, a FROM subquery can't name its siblings
/// (unless LATERAL), and correlated outer columns come qualified.</item>
/// <item><b>Inside a string literal or comment</b> — nothing; no popup while
/// typing prose. Inside an unterminated quoted identifier, names only.</item>
/// <item><b>Member access</b> — after <c>alias.</c>/<c>table.</c>/<c>schema.table.</c>,
/// only that relation's columns (or, after <c>schema.</c>, that schema's tables
/// and functions). A qualifier that names nothing known yields nothing: a
/// <c>missing.users</c> never borrows <c>public.users</c>' columns.</item>
/// <item><b>Table position</b> — after FROM/JOIN/INTO/UPDATE, only what can
/// legally go there: tables, schemas, the statement's CTE names, and keywords.</item>
/// <item><b>Predicate position</b> — after WHERE/ON/HAVING/GROUP BY/ORDER BY,
/// only the columns of the statement's sources (plus its aliases, CTEs,
/// functions and keywords). A column two sources share is offered once per
/// source, qualified (<c>u.id</c>, <c>o.id</c>), since the bare name would be
/// ambiguous — unless a USING/NATURAL join merged it.</item>
/// <item><b>Anywhere else</b> — everything, with the statement's own columns
/// floated to the top.</item>
/// </list>
/// Names resolve the way the server resolves them: a bare identifier is
/// case-folded, a quoted one is exact, and an unqualified table is looked up
/// along the search_path — two same-named tables in different schemas are two
/// relations, never one merged column list.
/// The catalog is read once (on connect / manual refresh) into an immutable
/// snapshot; per-keystroke work is a scan of the current statement and a few
/// dictionary lookups, with no server round-trip.
/// </summary>
public sealed class SqlCompletionProvider(SchemaService? schemaService) : IDisposable
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

    // What can legally open a statement, ordered by how often one actually does.
    // Only consulted at a statement-start caret (see SqlCompletionContext.
    // IsAtStatementStart); the order is the ranking, so SELECT outranks SET on a
    // typed "se" even though SET is the shorter match. A few of these aren't in
    // Keywords above (they're only ever leading words) — the rest dedupe against
    // it, the boosted copy winning because it is prepended.
    private static readonly string[] StatementStartKeywords =
    [
        "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER",
        "DROP", "EXPLAIN", "TRUNCATE", "BEGIN", "COMMIT", "ROLLBACK", "SET",
        "SHOW", "ANALYZE", "VACUUM", "REFRESH", "COMMENT", "GRANT", "REVOKE",
        "COPY", "CALL", "DO",
    ];

    // Everyday Postgres functions, curated rather than read from pg_proc — the
    // full catalog is thousands of overloads of noise. Inserted as "name()"
    // with the caret placed between the parens (see CompletionEdits.Plan).
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
        // arrays / sets
        "unnest", "generate_series", "array_length", "cardinality",
        "array_to_string", "string_to_array",
        // json / jsonb: construction, inspection, mutation, and expansion
        "to_json", "to_jsonb", "json_build_object", "jsonb_build_object",
        "json_build_array", "jsonb_build_array", "json_object", "jsonb_object",
        "row_to_json", "array_to_json",
        "jsonb_array_elements", "json_array_elements",
        "jsonb_array_elements_text", "jsonb_array_length", "json_array_length",
        "jsonb_each", "jsonb_each_text", "jsonb_object_keys",
        "jsonb_extract_path", "jsonb_extract_path_text", "jsonb_typeof",
        "jsonb_set", "jsonb_set_lax", "jsonb_insert", "jsonb_pretty",
        "jsonb_strip_nulls", "jsonb_populate_record", "json_populate_record",
        "jsonb_to_record", "jsonb_to_recordset",
        // jsonpath (SQL/JSON path queries)
        "jsonb_path_query", "jsonb_path_query_array", "jsonb_path_query_first",
        "jsonb_path_exists", "jsonb_path_match",
        // misc
        "md5", "gen_random_uuid", "pg_typeof",
    ];

    // Ranking bands. Current-statement items (its tables' columns, its aliases)
    // sit far above the rest so a match among them wins pre-selection; the base
    // bands only order ties within the catalog-wide list.
    private const double JoinConditionPriority = 200;
    // Statement-start keywords (SELECT, INSERT, WITH …) at a caret where nothing
    // else is grammatical. Above the current statement's own columns because at
    // that caret there is no statement yet — and each keyword's own rank falls
    // by its position in StatementStartKeywords, so "se" preselects SELECT
    // rather than the shorter SET.
    private const double StatementKeywordPriority = 120;
    private const double CurrentColumnPriority = 100;
    private const double AliasPriority = 90;
    private const double FkTablePriority = 15;
    private const double CtePriority = 20;
    private const double TablePriority = 10;
    private const double ColumnPriority = 5;
    private const double FunctionPriority = 3;
    private const double SchemaPriority = 1;
    // Types after "::": a user's own domains and enums before the built-ins.
    private const double UserTypePriority = 12;
    private const double TypePriority = 11;

    private readonly SchemaService? _schemaService = schemaService;

    // The one published view of the catalog. Replaced whole by Load, so a
    // keystroke never sees half of one refresh and half of another.
    private Snapshot _snapshot = Build(new CompletionCatalog([], [], [], [], null), new HashSet<string>());
    // Bumped by every refresh; a refresh that finishes after a newer one
    // started drops its result instead of overwriting the newer catalog.
    private int _refreshGeneration;

    /// <summary>
    /// Schemas to leave out of every candidate list — the sidebar's "Exclude
    /// from autocomplete", for the schemas another team owns in a database with
    /// dozens of them. Applied in <see cref="RefreshAsync"/> rather than at
    /// query time, so an excluded schema costs nothing per keystroke *and* its
    /// tables/columns/functions are never fetched at all: on a big catalog
    /// excluding schemas makes the refresh itself faster. Set by the host
    /// (persisted per connection); a change takes effect on the next refresh,
    /// which the host triggers when the toggle flips.
    /// </summary>
    public IReadOnlySet<string> ExcludedSchemas { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Set while the user's explicit transaction has run a <c>SET search_path</c>:
    /// the held connection now resolves short names along a path the catalog
    /// (read from a pooled connection) doesn't know. Until the transaction
    /// ends, a short name resolves only when exactly one schema has it — the
    /// same rule as an unknown path, and never a guess along the stale one.
    /// Outside a transaction a SET does not outlive its statement (the pool
    /// resets the session), so the host clears this when the transaction ends.
    /// </summary>
    public bool SessionSearchPathChanged { get; set; }

    /// <summary>What the published catalog is: fresh, or the last good one kept after a failed refresh.</summary>
    public CompletionCatalogStatus Status { get; private set; } = new(false, null);

    /// <summary>Raised when <see cref="Status"/> changes — from whichever thread the refresh finished on.</summary>
    public event Action<CompletionCatalogStatus>? StatusChanged;

    /// <summary>
    /// Reads the catalog and publishes it as one snapshot. Never throws for a
    /// read that fails: the previous snapshot stays in use (keywords and
    /// whatever was known keep completing) and <see cref="Status"/> says it is
    /// stale, with the reason. A newer refresh cancels an older one mid-read,
    /// and <see cref="Dispose"/> (the window closing, a connection switch)
    /// cancels whatever is in flight. The snapshot is built on the thread pool:
    /// for a catalog of a million columns that is half a second the UI thread
    /// no longer spends. True when this refresh's catalog was published.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (_schemaService is null || _lifetime.IsCancellationRequested)
        {
            return false;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        CancelQuietly(Interlocked.Exchange(ref _refreshCts, cts));
        try
        {
            var catalog = await ReadCatalogAsync(_schemaService, ExcludedSchemas, cts.Token);
            var excluded = ExcludedSchemas;
            var snapshot = await Task.Run(() => Build(catalog, excluded), cts.Token);
            if (generation != Volatile.Read(ref _refreshGeneration) || cts.IsCancellationRequested)
            {
                return false; // a newer refresh started meanwhile — its catalog wins
            }

            _snapshot = snapshot;
            SetStatus(new CompletionCatalogStatus(false, null));
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                SetStatus(new CompletionCatalogStatus(true, ex.Message));
            }

            return false;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _refreshCts, null, cts) == cts)
            {
                cts.Dispose();
            }
        }
    }

    /// <summary>Stops any refresh in flight and every later one: this provider's connection is going away.</summary>
    public void Dispose()
    {
        CancelQuietly(_lifetime);
        CancelQuietly(Interlocked.Exchange(ref _refreshCts, null));
    }

    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _refreshCts;

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished and disposed between the exchange and the cancel: nothing left to stop.
        }
    }

    private void SetStatus(CompletionCatalogStatus status)
    {
        if (status == Status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(status);
    }

    private static async Task<CompletionCatalog> ReadCatalogAsync(SchemaService schemaService, IReadOnlySet<string> excluded, CancellationToken ct)
    {
        var schemaNames = new List<string>();
        var tables = new List<CompletionTable>();
        var functions = new List<CompletionFunction>();

        foreach (var schema in await schemaService.GetSchemasAsync(ct))
        {
            // An excluded schema contributes nothing anywhere, and its catalog
            // queries are skipped with it.
            if (excluded.Contains(schema.Name))
            {
                continue;
            }

            schemaNames.Add(schema.Name);
            var names = await schemaService.GetRelationNamesAsync(schema.Name, ct);
            var columns = (await schemaService.GetAllColumnsAsync(schema.Name, ct))
                .GroupBy(c => c.Table, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<TableColumn>)[.. g], StringComparer.Ordinal);
            foreach (var name in names)
            {
                tables.Add(new CompletionTable(schema.Name, name, columns.GetValueOrDefault(name) ?? []));
            }

            foreach (var function in await schemaService.GetFunctionsAsync(schema.Name, ct))
            {
                functions.Add(new CompletionFunction(schema.Name, function));
            }
        }

        var foreignKeys = await schemaService.GetForeignKeysAsync(ct);
        var builtinFunctions = (await schemaService.GetFunctionsAsync("pg_catalog", ct))
            .Select(f => new CompletionFunction("pg_catalog", f))
            .ToList();
        var types = await schemaService.GetTypesAsync(ct);
        IReadOnlyList<string>? searchPath;
        try
        {
            searchPath = await schemaService.GetSearchPathAsync(ct);
        }
        catch (Npgsql.NpgsqlException)
        {
            searchPath = null;
        }

        return new CompletionCatalog(schemaNames, tables, functions, foreignKeys, searchPath)
        {
            BuiltinFunctions = builtinFunctions,
            Types = types,
        };
    }

    /// <summary>
    /// Publishes <paramref name="catalog"/> as what completion offers from now
    /// on. <see cref="RefreshAsync"/> ends here; tests and previews call it
    /// directly with a catalog built in memory. <see cref="ExcludedSchemas"/>
    /// applies here too, including to foreign keys, so an excluded schema can't
    /// come back through a JOIN suggestion.
    /// </summary>
    public void Load(CompletionCatalog catalog) => _snapshot = Build(catalog, ExcludedSchemas);

    private static Snapshot Build(CompletionCatalog catalog, IReadOnlySet<string> excluded)
    {
        var schemas = catalog.Schemas.Where(s => !excluded.Contains(s)).ToList();
        var tables = catalog.Tables.Where(t => !excluded.Contains(t.Schema)).ToList();
        var functions = catalog.Functions.Where(f => !excluded.Contains(f.Schema)).ToList();
        var foreignKeys = catalog.ForeignKeys
            .Where(fk => !excluded.Contains(fk.FromSchema) && !excluded.Contains(fk.ToSchema))
            .ToList();
        var searchPath = catalog.SearchPath;

        var keywordItems = Keywords.Select(k => new SqlCompletionData(k, SqlCompletionKind.Keyword)).ToList();
        var builtinFunctionItems = Functions
            .Select(f => new SqlCompletionData(f, SqlCompletionKind.Function, $"{f}()", FunctionPriority))
            .ToList();

        // Overloads collapse into one row per schema-qualified name, with every
        // signature in the tooltip; procedures are kept apart because they are
        // only callable after CALL, never inside an expression.
        var callableItems = new List<SqlCompletionData>();
        var procedureItems = new List<SqlCompletionData>();
        foreach (var group in functions.GroupBy(f => (f.Schema, f.Function.Name, IsProcedure: f.Function.Kind == 'p')))
        {
            var item = FunctionItem(group.Key.Schema, group.Key.Name, [.. group.Select(f => f.Function)], searchPath);
            (group.Key.IsProcedure ? procedureItems : callableItems).Add(item);
        }

        var baseItems = new List<SqlCompletionData>(keywordItems);
        baseItems.AddRange(builtinFunctionItems);
        // Keywords go *after* the catalog here: with nothing typed yet the list
        // renders in insertion order, and right after FROM/JOIN the point is the
        // tables, not SELECT/WHERE.
        var tableRefItems = new List<SqlCompletionData>();
        foreach (var schema in schemas)
        {
            // The bare name filters the list; the quote-if-needed form is what gets
            // inserted, so accepting a mixed-case object writes "Spells" not spells.
            var schemaItem = new SqlCompletionData(schema, SqlCompletionKind.Schema, SqlIdentifier.QuoteIfNeeded(schema), SchemaPriority);
            baseItems.Add(schemaItem);
            tableRefItems.Add(schemaItem);
        }

        foreach (var table in tables)
        {
            // Elsewhere a table completes to its bare name; in table position
            // (after FROM/JOIN) it completes schema-qualified ("public.users")
            // so the reference is unambiguous whatever the search_path is.
            baseItems.Add(TableItem(table.Schema, table.Name, qualified: false));
            tableRefItems.Add(TableItem(table.Schema, table.Name, qualified: true));
            foreach (var column in table.Columns)
            {
                baseItems.Add(ColumnItem(column.Column, column.DataType, table.Name, ColumnPriority));
            }
        }

        tableRefItems.AddRange(keywordItems);
        baseItems.AddRange(callableItems);

        var tablesByKey = new Dictionary<(string, string), CompletionTable>();
        var tablesByName = new Dictionary<string, List<CompletionTable>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            tablesByKey[(table.Schema, table.Name)] = table;
            if (!tablesByName.TryGetValue(table.Name, out var sameName))
            {
                tablesByName[table.Name] = sameName = [];
            }

            sameName.Add(table);
        }

        var predicateBase = keywordItems.Concat(builtinFunctionItems).Concat(callableItems).ToList();

        // Argument hints read every callable by name: pg_catalog's (searched
        // first by the server, whatever the search_path says) and the schemas'.
        var hintFunctions = new Dictionary<string, List<CompletionFunction>>(StringComparer.Ordinal);
        foreach (var function in catalog.BuiltinFunctions.Concat(functions))
        {
            if (!hintFunctions.TryGetValue(function.Function.Name, out var overloads))
            {
                hintFunctions[function.Function.Name] = overloads = [];
            }

            overloads.Add(function);
        }

        var typeItems = catalog.Types.Count == 0
            ? FallbackTypeItems
            : Dedupe(catalog.Types.Where(t => !excluded.Contains(t.Schema)).Select(t => TypeItem(t, searchPath)));

        return new Snapshot(
            tables,
            tablesByKey,
            tablesByName,
            callableItems,
            procedureItems,
            foreignKeys,
            searchPath,
            new HashSet<string>(excluded, StringComparer.Ordinal),
            Dedupe(baseItems),
            Dedupe(tableRefItems),
            Dedupe(predicateBase),
            hintFunctions,
            typeItems);
    }

    // A type as a cast writes it: pg_catalog's by the name format_type gives
    // when that is one word ("integer", "jsonb"), else by pg_type's own
    // ("timestamptz", "varchar") — both are valid in a cast; a user type
    // schema-qualified when its schema isn't on the search_path.
    private static SqlCompletionData TypeItem(DataTypeInfo type, IReadOnlyList<string>? searchPath)
    {
        if (type.Schema == "pg_catalog")
        {
            var name = type.DisplayName.Contains(' ') ? type.Name : type.DisplayName;
            return new SqlCompletionData(name, SqlCompletionKind.Type, name, TypePriority)
            {
                Detail = name == type.DisplayName ? null : type.DisplayName,
                DescriptionText = "type",
            };
        }

        var onPath = searchPath?.Contains(type.Schema) ?? type.Schema == "public";
        var insert = onPath
            ? SqlIdentifier.QuoteIfNeeded(type.Name)
            : $"{SqlIdentifier.QuoteIfNeeded(type.Schema)}.{SqlIdentifier.QuoteIfNeeded(type.Name)}";
        var kind = type.Kind switch
        {
            'd' => "domain",
            'e' => "enum",
            'c' => "composite type",
            'r' => "range type",
            'm' => "multirange type",
            _ => "type",
        };
        return new SqlCompletionData(type.Name, SqlCompletionKind.Type, insert, UserTypePriority)
        {
            Detail = type.Schema,
            DescriptionText = kind,
        };
    }

    // The everyday types, for a cast typed before the catalog has been read.
    private static readonly IReadOnlyList<SqlCompletionData> FallbackTypeItems =
    [
        .. new[]
        {
            "integer", "bigint", "smallint", "numeric", "real", "float8", "text", "varchar", "boolean", "date",
            "time", "timestamp", "timestamptz", "interval", "uuid", "json", "jsonb", "bytea", "inet", "cidr",
            "money", "xml", "tsvector", "tsquery", "regclass", "oid",
        }.Select(t => new SqlCompletionData(t, SqlCompletionKind.Type, t, TypePriority) { DescriptionText = "type" }),
    ];

    /// <summary>
    /// The argument hint for the call around <paramref name="caret"/>: the
    /// overloads that fit the argument being typed, each with its parameter
    /// marked. Null outside a call, or for a name no callable has. A
    /// qualified name looks in that schema only; a bare one in pg_catalog and
    /// the search_path (every schema, when the path is unknown).
    /// </summary>
    public (SqlCallSite Site, IReadOnlyList<SignatureHint> Hints)? GetSignatureHints(string sql, int caret)
    {
        if (SqlCallSite.At(sql, caret) is not { } site || site.Name.Count == 0)
        {
            return null;
        }

        var snapshot = _snapshot;
        if (!snapshot.HintFunctions.TryGetValue(site.Name[^1], out var overloads))
        {
            return null;
        }

        IEnumerable<CompletionFunction> visible;
        if (site.Name.Count >= 2)
        {
            visible = overloads.Where(o => o.Schema == site.Name[^2]);
        }
        else
        {
            var path = SessionSearchPathChanged ? null : snapshot.SearchPath;
            visible = overloads
                .Where(o => o.Schema == "pg_catalog" || path is null || path.Contains(o.Schema))
                .OrderBy(o => o.Schema == "pg_catalog" ? -1 : path?.ToList().IndexOf(o.Schema) ?? 0);
        }

        var hints = SignatureHints.For(site, visible.Select(o => (o.Schema, o.Function)));
        return hints.Count == 0 ? null : (site, hints);
    }

    // A catalog callable (all overloads of one schema.name): inserts as
    // "name()" — schema-qualified when the schema isn't on the search_path, so
    // the call resolves to the function that was picked — with every signature
    // in the tooltip, doubling as a lightweight parameter hint.
    private static SqlCompletionData FunctionItem(string schema, string name, IReadOnlyList<FunctionInfo> overloads, IReadOnlyList<string>? searchPath)
    {
        var onPath = searchPath?.Contains(schema) ?? schema == "public";
        var callName = onPath
            ? SqlIdentifier.QuoteIfNeeded(name)
            : $"{SqlIdentifier.QuoteIfNeeded(schema)}.{SqlIdentifier.QuoteIfNeeded(name)}";
        var signatures = overloads.Select(f => $"{FunctionSignatureFormatter.KindLabel(f)} · {FunctionSignatureFormatter.Format(f)}");
        return new SqlCompletionData(name, SqlCompletionKind.Function, $"{callName}()", FunctionPriority)
        {
            Detail = schema,
            DescriptionText = string.Join("\n", signatures),
        };
    }

    private static SqlCompletionData TableItem(string schema, string table, bool qualified, double priority = TablePriority) =>
        new(table, SqlCompletionKind.Table,
            qualified ? $"{SqlIdentifier.QuoteIfNeeded(schema)}.{SqlIdentifier.QuoteIfNeeded(table)}" : SqlIdentifier.QuoteIfNeeded(table),
            priority)
        {
            AliasTable = table,
            Detail = schema,
        };

    /// <summary>
    /// The candidates to show for the caret at <paramref name="caretOffset"/> in
    /// <paramref name="sql"/> — nothing inside strings/comments, member-access
    /// columns/tables after a <c>qualifier.</c>, tables in table position,
    /// otherwise the full catalog with the current statement's columns floated
    /// to the top.
    /// </summary>
    public IReadOnlyList<SqlCompletionData> GetCompletionData(string sql, int caretOffset)
    {
        var snapshot = _snapshot;
        var (start, end) = SqlCompletionContext.CompletionStatementSpan(sql, caretOffset);
        var statement = sql[start..end];
        var caret = Math.Clamp(caretOffset - start, 0, statement.Length);

        var context = SqlCompletionContext.GetCaretContext(statement, caret);
        if (context.InStringOrComment)
        {
            return [];
        }

        var items = Candidates(snapshot, statement, caret, context);

        // Inside "…" the user is spelling a name; keywords can't go there.
        return context.InQuotedIdentifier
            ? [.. items.Where(i => i.Kind != SqlCompletionKind.Keyword)]
            : items;
    }

    private IReadOnlyList<SqlCompletionData> Candidates(Snapshot snapshot, string statement, int caret, SqlCompletionContext.CaretContext context)
    {
        if (IsTypePosition(statement, caret))
        {
            return snapshot.TypeItems;
        }

        var scope = Scope.At(statement, caret);
        var chain = SqlCompletionContext.GetQualifierChainBeforeCaret(statement, caret);
        if (chain.Count > 0)
        {
            return GetMemberCompletions(snapshot, chain, statement, scope);
        }

        if (SqlCompletionContext.IsAfterKeyword(statement, caret, "call"))
        {
            return Dedupe(snapshot.ProcedureItems.Concat(snapshot.TableRefItems.Where(i => i.Kind == SqlCompletionKind.Schema)));
        }

        if (scope.Block is { } contextBlock && ColumnListCompletions(snapshot, statement, contextBlock, caret) is { } columnList)
        {
            return columnList;
        }

        return context.Clause switch
        {
            SqlClause.JoinTableRef when SqlCompletionContext.IsAfterCompleteJoinTarget(statement, caret) =>
                BuildTableRefCompletions(snapshot, statement, scope, JoinKeywordBoostItems),
            // A finished FROM item: the clause words that can follow it. (A
            // finished JOIN target owes its ON/USING first, above.)
            SqlClause.TableRef or SqlClause.FromTableRef
                when SqlCompletionContext.IsAfterCompleteFromItem(statement, caret) =>
                BuildTableRefCompletions(snapshot, statement, scope, FromItemFollowItems),
            SqlClause.TableRef or SqlClause.FromTableRef => BuildTableRefCompletions(snapshot, statement, scope, boosted: []),
            SqlClause.JoinTableRef => BuildTableRefCompletions(snapshot, statement, scope, FkNeighborItems(snapshot, statement, scope)),
            SqlClause.Predicate when SqlCompletionContext.IsAfterOnKeyword(statement, caret) =>
                GetJoinConditionCompletions(snapshot, statement, caret, scope),
            SqlClause.Predicate => GetPredicateCompletions(snapshot, statement, scope),
            // A select list whose block already names its sources can only
            // reference those (and what is around it): not another branch's
            // tables, not the rest of the catalog.
            SqlClause.ColumnRef when scope.Block is { Kind: not SqlBlockKind.Values, Sources.Count: > 0 } =>
                GetPredicateCompletions(snapshot, statement, scope),
            _ => GetGeneralCompletions(snapshot, statement, scope, SqlCompletionContext.IsAtStatementStart(statement, caret)),
        };
    }

    // Where only a type name can go: right after "::", or after AS inside
    // CAST( … ). (A "schema." typed after "::" still lands here, and the
    // schema's user types come first.)
    private static bool IsTypePosition(string statement, int caret)
    {
        var wordStart = caret;
        while (wordStart > 0 && SqlLexer.IsIdentPart(statement[wordStart - 1]))
        {
            wordStart--;
        }

        var before = wordStart;
        while (before > 0 && char.IsWhiteSpace(statement[before - 1]))
        {
            before--;
        }

        if (before >= 2 && statement[before - 1] == ':' && statement[before - 2] == ':')
        {
            return true;
        }

        return SqlCompletionContext.IsAfterKeyword(statement, caret, "as")
            && SqlCallSite.At(statement, caret) is { Name: [var name] } && name == "cast";
    }

    // After "qualifier.": the columns of whatever the chain names. One part is
    // an alias, an unaliased source's table name, a CTE, a table found along
    // the search_path, or a schema (→ its tables and functions); two parts are
    // schema.table, exactly. A qualifier that names something the catalog
    // doesn't have yields nothing rather than a guess.
    private IReadOnlyList<SqlCompletionData> GetMemberCompletions(Snapshot snapshot, IReadOnlyList<SqlCompletionContext.NamePart> chain, string statement, Scope scope)
    {
        if (chain.Count >= 2)
        {
            return snapshot.TablesByKey.TryGetValue((chain[^2].Name, chain[^1].Name), out var exact)
                ? ColumnItems(exact.Columns.Select(c => new SourceColumn(c.Column, c.DataType, exact.Name)))
                : [];
        }

        if (scope.Unknown)
        {
            return [];
        }

        var qualifier = chain[0].Name;
        if (scope.Block is { } block)
        {
            if (qualifier == "excluded" && block.SeesExcluded(scope.Caret) && block.Target is { } target)
            {
                return ColumnItems(ColumnsOf(snapshot, block, target, []) ?? []);
            }

            // Innermost level first, alias before bare table name within each:
            // the order the server resolves a qualifier in.
            foreach (var level in SqlScopeModel.VisibleSources(block))
            {
                if ((level.FirstOrDefault(s => s.Alias == qualifier) ?? level.FirstOrDefault(s => s.Alias is null && s.Name == qualifier)) is { } source)
                {
                    return ColumnItems(ColumnsOf(snapshot, block, source, []) ?? []);
                }
            }

            if (FindCte(block, qualifier) is { } visibleCte)
            {
                return ColumnItems(CteOutput(snapshot, visibleCte, []) ?? []);
            }
        }
        else
        {
            var ctes = SqlCompletionContext.ExtractCteDefinitions(statement);
            var sources = SqlCompletionContext.ExtractTables(statement);
            foreach (var source in sources.Where(s => s.Alias == qualifier).Concat(sources.Where(s => s.Alias is null && s.Table == qualifier)))
            {
                return ColumnItems(SourceColumns(snapshot, source, ctes) ?? []);
            }

            if (CteColumns(snapshot, qualifier, ctes) is { } cteColumns)
            {
                return ColumnItems(cteColumns);
            }
        }

        if (Resolve(snapshot, "", qualifier) is { } direct)
        {
            return ColumnItems(direct.Columns.Select(c => new SourceColumn(c.Column, c.DataType, direct.Name)));
        }

        // schema. → the schema's tables and functions
        var items = new List<SqlCompletionData>();
        foreach (var table in snapshot.Tables)
        {
            if (table.Schema == qualifier)
            {
                items.Add(TableItem(table.Schema, table.Name, qualified: false));
            }
        }

        foreach (var function in snapshot.CallableItems)
        {
            if (function.Detail == qualifier)
            {
                // Qualified by the typed "schema." already — insert the bare name.
                items.Add(new SqlCompletionData(function.Text, SqlCompletionKind.Function, $"{SqlIdentifier.QuoteIfNeeded(function.Text)}()", FunctionPriority)
                {
                    Detail = function.Detail,
                    DescriptionText = function.DescriptionText,
                });
            }
        }

        return items;
    }

    // Ranked copies of StatementStartKeywords: priority falls by one per position
    // so the list's own order decides ties among equally-good prefix matches.
    private static readonly IReadOnlyList<SqlCompletionData> StatementStartItems =
        StatementStartKeywords
            .Select((keyword, index) => new SqlCompletionData(keyword, SqlCompletionKind.Keyword, keyword, StatementKeywordPriority - index))
            .ToList();

    private static readonly IReadOnlyList<SqlCompletionData> JoinKeywordBoostItems =
    [
        new SqlCompletionData("ON", SqlCompletionKind.Keyword, "ON", JoinConditionPriority),
        new SqlCompletionData("USING", SqlCompletionKind.Keyword, "USING", JoinConditionPriority),
    ];

    // What can follow a finished FROM item, most common first: "FROM users u w"
    // should preselect WHERE, not WHEN/WITH, which can't go there at all.
    // Ranked by position like StatementStartItems, above the tables (which
    // can't follow an item either, only a comma can bring one back).
    private static readonly IReadOnlyList<SqlCompletionData> FromItemFollowItems =
        new[]
        {
            "WHERE", "JOIN", "LEFT", "INNER", "GROUP", "ORDER", "LIMIT", "CROSS",
            "RIGHT", "FULL", "NATURAL", "HAVING", "OFFSET", "UNION", "EXCEPT", "INTERSECT",
        }
        .Select((keyword, index) => new SqlCompletionData(keyword, SqlCompletionKind.Keyword, keyword, StatementKeywordPriority - index))
        .ToList();

    // Table position (after FROM/INTO/UPDATE …): only what can be a table there —
    // the statement's CTEs first, then schemas + tables (+ keywords, so
    // "JOIN"/"WHERE" still complete after "FROM users "). No columns.
    private static IReadOnlyList<SqlCompletionData> BuildTableRefCompletions(Snapshot snapshot, string statement, Scope scope, IEnumerable<SqlCompletionData> boosted)
    {
        var items = new List<SqlCompletionData>();
        foreach (var cte in scope.CteNames(statement))
        {
            items.Add(new SqlCompletionData(cte, SqlCompletionKind.Cte, SqlIdentifier.QuoteIfNeeded(cte), CtePriority));
        }

        items.AddRange(boosted);
        return Merge(items, snapshot.TableRefItems);
    }

    // Every table FK-adjacent to a table already in the statement (either side of
    // the relationship — the new table can be the "many" or the "one" side),
    // excluding tables the statement already references. The graph walk itself is
    // pure Core logic (ForeignKeyMatcher, unit-tested there).
    private List<SqlCompletionData> FkNeighborItems(Snapshot snapshot, string statement, Scope scope)
    {
        var statementTables = ResolvedReferences(snapshot, scope.Relations(statement, int.MaxValue));
        var items = new List<SqlCompletionData>();
        foreach (var (neighborSchema, neighborTable) in ForeignKeyMatcher.FindJoinCandidates(statementTables, snapshot.ForeignKeys))
        {
            var item = TableItem(neighborSchema, neighborTable, qualified: true, FkTablePriority);
            items.Add(new SqlCompletionData(item.Text, item.Kind, item.InsertText, item.Priority)
            {
                AliasTable = item.AliasTable,
                Detail = item.Detail,
                DescriptionText = "table · FK match",
            });
        }

        return items;
    }

    // The join condition suggestion after ON: pairs the table this ON belongs
    // to — the last one joined *before the caret*, so editing an early ON
    // ignores the JOINs written after it — with the closest earlier table it has
    // a direct FK to, and offers "child.fk_col = parent.pk_col" as the single
    // top item.
    private IReadOnlyList<SqlCompletionData> GetJoinConditionCompletions(Snapshot snapshot, string statement, int caret, Scope scope)
    {
        var predicateItems = GetPredicateCompletions(snapshot, statement, scope);
        var statementTables = ResolvedReferences(snapshot, scope.Relations(statement, caret));
        var conditions = ForeignKeyMatcher.BuildJoinConditions(statementTables, snapshot.ForeignKeys);
        if (conditions.Count == 0)
        {
            return predicateItems;
        }

        // One row per constraint, the closest table's first: two FKs between the
        // same pair are two different joins, and the row names which is which.
        var items = new List<SqlCompletionData>(predicateItems.Count + conditions.Count);
        for (var i = 0; i < conditions.Count; i++)
        {
            var (condition, constraint) = conditions[i];
            items.Add(new SqlCompletionData(condition, SqlCompletionKind.JoinCondition, condition, JoinConditionPriority - i)
            {
                Detail = constraint,
                DescriptionText = constraint is null ? "FK join condition" : $"FK join condition · {constraint}",
            });
        }

        items.AddRange(predicateItems);
        return Dedupe(items);
    }

    // Statement table refs with their schema filled in from the catalog where
    // the name resolves, so FK matching compares one relation with another
    // rather than bare names across schemas.
    private List<TableReference> ResolvedReferences(Snapshot snapshot, IReadOnlyList<SqlCompletionContext.TableRef> tables) =>
        [.. tables.Select(t => Resolve(snapshot, t.Schema, t.Table) is { } resolved
            ? new TableReference(resolved.Schema, resolved.Name, t.Alias)
            : new TableReference(t.Schema, t.Table, t.Alias))];

    // Bare identifier: the whole catalog, with the statement's own columns
    // hoisted to the front (and top priority), plus its aliases and CTE names.
    // At a statement-start caret the leading keywords go in front of even those.
    private IReadOnlyList<SqlCompletionData> GetGeneralCompletions(Snapshot snapshot, string statement, Scope scope, bool atStatementStart)
    {
        var items = CollectStatementItems(snapshot, statement, scope, out _);
        if (atStatementStart)
        {
            // Prepended, so the merge below keeps these ranked copies over the
            // flat-priority ones already in BaseItems.
            items.InsertRange(0, StatementStartItems);
        }

        return Merge(items, snapshot.BaseItems);
    }

    // Predicate/row position (WHERE, ON, HAVING, GROUP/ORDER BY, USING): only the
    // statement's sources' columns can be named here. When the statement has
    // sources but none of them resolves, the catalog's columns still stay out —
    // an unknown table is no reason to offer every column in the database; the
    // full catalog is the fallback only for a statement with no sources at all.
    private IReadOnlyList<SqlCompletionData> GetPredicateCompletions(Snapshot snapshot, string statement, Scope scope)
    {
        var items = CollectStatementItems(snapshot, statement, scope, out var sourceCount);
        return Merge(items, sourceCount == 0 ? snapshot.BaseItems : snapshot.PredicateBaseItems);
    }

    // A column as one source exposes it.
    private readonly record struct SourceColumn(string Name, string? DataType, string Owner);

    // The current statement's own contributions: its aliases, CTE names, and
    // every source's columns (top priority). A name two sources share is
    // offered once per source, qualified by that source's alias — the bare name
    // would be an "ambiguous column" error — unless a USING/NATURAL join
    // merged it into a single output column.
    private List<SqlCompletionData> CollectStatementItems(Snapshot snapshot, string statement, Scope scope, out int sourceCount)
    {
        if (scope.Unknown)
        {
            // Nested past what the scope reader follows: nothing about the
            // sources is known, so offer none, and don't open the catalog either.
            sourceCount = 1;
            return [];
        }

        if (scope.Block is { } block)
        {
            return CollectScopeItems(snapshot, block, scope.Caret, out sourceCount);
        }

        var items = new List<SqlCompletionData>();
        var ctes = SqlCompletionContext.ExtractCteDefinitions(statement);
        var sources = SqlCompletionContext.ExtractTables(statement);
        sourceCount = sources.Count;

        var resolved = new List<(string Label, IReadOnlyList<SourceColumn> Columns)>();
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Alias is not null)
            {
                items.Add(new SqlCompletionData(source.Alias, SqlCompletionKind.Alias, SqlIdentifier.QuoteIfNeeded(source.Alias), AliasPriority)
                {
                    Detail = source.Table,
                    DescriptionText = $"alias for {source.Table}",
                });
            }

            // One source per name it is addressed by: a self-join's two aliases
            // are two sources, the same unaliased table written twice is one.
            var label = source.Alias ?? source.Table;
            if (seenSources.Add(label) && SourceColumns(snapshot, source, ctes) is { } columns)
            {
                resolved.Add((label, columns));
            }
        }

        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, columns) in resolved)
        {
            foreach (var name in columns.Select(c => c.Name).Distinct(StringComparer.Ordinal))
            {
                owners[name] = owners.GetValueOrDefault(name) + 1;
            }
        }

        var merged = SqlCompletionContext.ExtractUsingColumns(statement, out var natural);
        foreach (var (label, columns) in resolved)
        {
            foreach (var column in columns)
            {
                if (owners[column.Name] > 1 && !natural && !merged.Contains(column.Name))
                {
                    var qualified = $"{SqlIdentifier.QuoteIfNeeded(label)}.{SqlIdentifier.QuoteIfNeeded(column.Name)}";
                    items.Add(new SqlCompletionData(column.Name, SqlCompletionKind.Column, qualified, CurrentColumnPriority)
                    {
                        DisplayText = $"{label}.{column.Name}",
                        Detail = column.DataType,
                        DescriptionText = $"column · {column.Owner} · also in another source",
                    });
                }
                else
                {
                    items.Add(ColumnItem(column.Name, column.DataType, column.Owner, CurrentColumnPriority));
                }
            }
        }

        foreach (var cte in ctes)
        {
            items.Add(new SqlCompletionData(cte.Name, SqlCompletionKind.Cte, SqlIdentifier.QuoteIfNeeded(cte.Name), CtePriority));
        }

        return items;
    }

    // The columns one FROM/UPDATE/INTO source exposes: a CTE of the statement
    // (which shadows a same-named table even when its columns can't be derived)
    // or a catalog relation. Null when the source resolves to nothing known.
    private List<SourceColumn>? SourceColumns(Snapshot snapshot, SqlCompletionContext.TableRef source, IReadOnlyList<SqlCompletionContext.CteDefinition> ctes)
    {
        if (source.Schema.Length == 0 && ctes.Any(c => c.Name == source.Table))
        {
            return CteColumns(snapshot, source.Table, ctes) is { Count: > 0 } cteColumns ? cteColumns : null;
        }

        return Resolve(snapshot, source.Schema, source.Table) is { } table
            ? [.. table.Columns.Select(c => new SourceColumn(c.Column, c.DataType, table.Name))]
            : null;
    }

    // Outer-level columns (a correlated reference from inside a subquery):
    // offered qualified, just under the block's own columns.
    private const double OuterColumnPriority = 95;

    // The block the caret is in (from SqlScopeModel), for everything that asks
    // "what can be named here". Block is null when the statement holds no query
    // the scope reader follows (DDL, SET …): those keep the whole-statement
    // reading. Unknown means the caret is in a query nested too deep to read,
    // where nothing is offered rather than a guess.
    private sealed record Scope(SqlBlock? Block, bool Unknown, int Caret)
    {
        public static Scope At(string statement, int caret)
        {
            var block = SqlScopeModel.Parse(statement).BlockAt(caret, out var unknown);
            return new Scope(block, unknown, caret);
        }

        public IEnumerable<string> CteNames(string statement)
        {
            if (Unknown)
            {
                return [];
            }

            return Block is { } block
                ? SqlScopeModel.VisibleCtes(block).Select(c => c.Name).Distinct(StringComparer.Ordinal)
                : SqlCompletionContext.ExtractCteNames(statement);
        }

        // The block's own relations (no derived tables or functions) that
        // start before `before` — what FK matching pairs a JOIN against.
        public IReadOnlyList<SqlCompletionContext.TableRef> Relations(string statement, int before)
        {
            if (Unknown)
            {
                return [];
            }

            if (Block is not { } block)
            {
                return SqlCompletionContext.ExtractTables(before < statement.Length ? statement[..before] : statement);
            }

            return [.. block.Sources
                .Where(s => s.Derived is null && !s.IsFunction && s.Name.Length > 0 && s.Start < before)
                .Select(s => new SqlCompletionContext.TableRef(s.Schema, s.Name, s.Alias))];
        }
    }

    // The block's own contributions plus the levels around it: aliases, CTE
    // names, and every visible source's columns. The block's own columns
    // follow the ambiguity rule (a name two sources share is offered per
    // source, qualified, unless USING/NATURAL merged it); an outer level's
    // columns are always offered qualified, since a correlated reference that
    // happens to share a name with an inner column would bind to the inner one.
    private List<SqlCompletionData> CollectScopeItems(Snapshot snapshot, SqlBlock block, int caret, out int sourceCount)
    {
        var items = new List<SqlCompletionData>();
        var levels = SqlScopeModel.VisibleSources(block);
        sourceCount = levels.Sum(l => l.Count);

        // ORDER BY / GROUP BY may name the select list's output columns; WHERE
        // and HAVING may not, which is why this is keyed on the clause.
        if (block.Kind == SqlBlockKind.Select && block.ClauseAt(caret) is "order" or "group")
        {
            foreach (var output in block.Output)
            {
                if (output.Name is { } name && output.RefColumn != name)
                {
                    items.Add(new SqlCompletionData(name, SqlCompletionKind.Column, SqlIdentifier.QuoteIfNeeded(name), CurrentColumnPriority)
                    {
                        Detail = "output",
                        DescriptionText = "output column of this SELECT",
                    });
                }
            }
        }

        // ON CONFLICT … DO UPDATE: "excluded" is the row the INSERT proposed.
        if (block.SeesExcluded(caret) && block.Target is { } conflictTarget
            && ColumnsOf(snapshot, block, conflictTarget, []) is { } excludedColumns)
        {
            items.Add(new SqlCompletionData("excluded", SqlCompletionKind.Alias, "excluded", AliasPriority)
            {
                Detail = conflictTarget.Name,
                DescriptionText = "the row proposed for insertion",
            });
            foreach (var column in excludedColumns)
            {
                items.Add(new SqlCompletionData(column.Name, SqlCompletionKind.Column, $"excluded.{SqlIdentifier.QuoteIfNeeded(column.Name)}", OuterColumnPriority)
                {
                    DisplayText = $"excluded.{column.Name}",
                    Detail = column.DataType,
                    DescriptionText = "column · proposed row",
                });
            }
        }

        var shadowed = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < levels.Count; depth++)
        {
            var resolved = new List<(string Label, IReadOnlyList<SourceColumn> Columns)>();
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in levels[depth])
            {
                var label = source.Label;
                if (label.Length == 0 || shadowed.Contains(label) || !seenLabels.Add(label))
                {
                    continue;
                }

                if (source.Alias is not null)
                {
                    var target = source.Derived is null ? source.Name : "subquery";
                    items.Add(new SqlCompletionData(source.Alias, SqlCompletionKind.Alias, SqlIdentifier.QuoteIfNeeded(source.Alias), AliasPriority)
                    {
                        Detail = target,
                        DescriptionText = depth == 0 ? $"alias for {target}" : $"alias for {target} · outer query",
                    });
                }

                if (ColumnsOf(snapshot, block, source, []) is { } columns)
                {
                    resolved.Add((label, columns));
                }
            }

            if (depth == 0)
            {
                AddBlockColumns(items, block, resolved);
            }
            else
            {
                foreach (var (label, columns) in resolved)
                {
                    foreach (var column in columns)
                    {
                        items.Add(new SqlCompletionData(column.Name, SqlCompletionKind.Column,
                            $"{SqlIdentifier.QuoteIfNeeded(label)}.{SqlIdentifier.QuoteIfNeeded(column.Name)}", OuterColumnPriority)
                        {
                            DisplayText = $"{label}.{column.Name}",
                            Detail = column.DataType,
                            DescriptionText = $"column · {column.Owner} · outer query",
                        });
                    }
                }
            }

            shadowed.UnionWith(seenLabels);
        }

        foreach (var cte in SqlScopeModel.VisibleCtes(block).Select(c => c.Name).Distinct(StringComparer.Ordinal))
        {
            items.Add(new SqlCompletionData(cte, SqlCompletionKind.Cte, SqlIdentifier.QuoteIfNeeded(cte), CtePriority));
        }

        return items;
    }

    private static void AddBlockColumns(List<SqlCompletionData> items, SqlBlock block, List<(string Label, IReadOnlyList<SourceColumn> Columns)> resolved)
    {
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, columns) in resolved)
        {
            foreach (var name in columns.Select(c => c.Name).Distinct(StringComparer.Ordinal))
            {
                owners[name] = owners.GetValueOrDefault(name) + 1;
            }
        }

        var natural = block.Sources.Any(s => s.Join == SqlJoinKind.Natural);
        var merged = block.Sources.SelectMany(s => s.UsingColumns).ToHashSet(StringComparer.Ordinal);
        foreach (var (label, columns) in resolved)
        {
            foreach (var column in columns)
            {
                if (owners[column.Name] > 1 && !natural && !merged.Contains(column.Name))
                {
                    items.Add(new SqlCompletionData(column.Name, SqlCompletionKind.Column,
                        $"{SqlIdentifier.QuoteIfNeeded(label)}.{SqlIdentifier.QuoteIfNeeded(column.Name)}", CurrentColumnPriority)
                    {
                        DisplayText = $"{label}.{column.Name}",
                        Detail = column.DataType,
                        DescriptionText = $"column · {column.Owner} · also in another source",
                    });
                }
                else
                {
                    items.Add(ColumnItem(column.Name, column.DataType, column.Owner, CurrentColumnPriority));
                }
            }
        }
    }

    // The positions where only a bare column of one particular relation can
    // be written, so the list is exactly those columns and nothing else:
    // INSERT INTO t (…) and ON CONFLICT (…) take the target's, a SET
    // assignment's left side too (unqualified — SET t.col is an error), and a
    // JOIN … USING (…) the columns both sides of *that* join have. A column
    // already listed is left out. Null when the caret is in none of them.
    private List<SqlCompletionData>? ColumnListCompletions(Snapshot snapshot, string statement, SqlBlock block, int caret)
    {
        if (block.Target is { } target
            && ((block.Kind == SqlBlockKind.Insert && (block.InsertColumns?.Contains(caret) == true || block.ConflictTarget?.Contains(caret) == true))
                || (block.Kind is SqlBlockKind.Update or SqlBlockKind.Insert && block.ClauseAt(caret) == "set"
                    && SqlScopeModel.IsAssignmentTarget(statement, block, caret))))
        {
            var listed = block.InsertColumns is { } list && list.Contains(caret) ? NamesListed(statement, list, caret)
                : block.ConflictTarget is { } conflict && conflict.Contains(caret) ? NamesListed(statement, conflict, caret)
                : AssignedNames(statement, block, caret);
            return BareColumnItems(ColumnsOf(snapshot, block, target, []), listed);
        }

        for (var i = 0; i < block.Sources.Count; i++)
        {
            var source = block.Sources[i];
            if (source.UsingSpan is not { } span || !span.Contains(caret))
            {
                continue;
            }

            var left = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < i; j++)
            {
                foreach (var column in ColumnsOf(snapshot, block, block.Sources[j], []) ?? [])
                {
                    left.Add(column.Name);
                }
            }

            var shared = ColumnsOf(snapshot, block, source, [])?.Where(c => left.Contains(c.Name)).ToList();
            return BareColumnItems(shared, NamesListed(statement, span, caret));
        }

        return null;
    }

    private static List<SqlCompletionData> BareColumnItems(IReadOnlyList<SourceColumn>? columns, IReadOnlySet<string> listed) =>
        [.. (columns ?? []).Where(c => !listed.Contains(c.Name)).Select(c => ColumnItem(c.Name, c.DataType, c.Owner, CurrentColumnPriority))];

    // The names already written in a parenthesized list, except the one the caret is typing.
    private static HashSet<string> NamesListed(string statement, SqlSpan span, int caret)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in SqlLexer.Tokenize(statement, span.Start, span.End))
        {
            if (token.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier && !(token.Start <= caret && caret <= token.End))
            {
                names.Add(SqlLexer.IdentifierName(statement, token));
            }
        }

        return names;
    }

    // The columns a SET list already assigns (each name right after SET or a top-level comma).
    private static HashSet<string> AssignedNames(string statement, SqlBlock block, int caret)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var setEnd = block.Clauses.LastOrDefault(m => m.Keyword == "set" && m.End <= caret).End;
        var expectName = true;
        var depth = 0;
        foreach (var token in SqlLexer.Tokenize(statement, setEnd, statement.Length))
        {
            if (token.IsTrivia)
            {
                continue;
            }

            if (token.Kind is SqlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind is SqlTokenKind.CloseParen)
            {
                depth--;
            }
            else if (token.Kind == SqlTokenKind.Comma && depth == 0)
            {
                expectName = true;
                continue;
            }
            else if (expectName && depth == 0 && token.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
            {
                var word = SqlLexer.IdentifierName(statement, token);
                if (token.Kind == SqlTokenKind.Word && word is "where" or "from" or "returning")
                {
                    break;
                }

                if (!(token.Start <= caret && caret <= token.End))
                {
                    names.Add(word);
                }
            }

            expectName = false;
        }

        return names;
    }

    // The columns one source of `context` exposes: a derived table's output, a
    // CTE's (a visible CTE shadows a same-named table even when its columns
    // can't be derived), or a catalog relation's; renamed by a column alias
    // list. Null when nothing about them is known. `visiting` cuts a CTE that
    // (through stars) reaches itself.
    private List<SourceColumn>? ColumnsOf(Snapshot snapshot, SqlBlock context, SqlSource source, HashSet<SqlCte> visiting)
    {
        List<SourceColumn>? columns;
        if (source.Derived is { } derived)
        {
            columns = OutputOf(snapshot, derived, source.Label, visiting);
        }
        else if (source.IsFunction)
        {
            columns = null;
        }
        else if (source.Schema.Length == 0 && FindCte(context, source.Name) is { } cte)
        {
            columns = CteOutput(snapshot, cte, visiting);
        }
        else
        {
            columns = Resolve(snapshot, source.Schema, source.Name) is { } table
                ? [.. table.Columns.Select(c => new SourceColumn(c.Column, c.DataType, table.Name))]
                : null;
        }

        if (source.ColumnAliases is { Count: > 0 } aliases)
        {
            var renamed = new List<SourceColumn>();
            for (var i = 0; i < aliases.Count; i++)
            {
                var type = columns is not null && i < columns.Count ? columns[i].DataType : null;
                renamed.Add(new SourceColumn(aliases[i], type, source.Label));
            }

            if (columns is not null)
            {
                renamed.AddRange(columns.Skip(aliases.Count));
            }

            return renamed;
        }

        return columns;
    }

    private static SqlCte? FindCte(SqlBlock block, string name) =>
        SqlScopeModel.VisibleCtes(block).FirstOrDefault(c => c.Name == name);

    private List<SourceColumn>? CteOutput(Snapshot snapshot, SqlCte cte, HashSet<SqlCte> visiting)
    {
        if (!visiting.Add(cte))
        {
            return null;
        }

        try
        {
            var body = OutputOf(snapshot, cte.Body, cte.Name, visiting);
            if (cte.DeclaredColumns is not { Count: > 0 } declared)
            {
                return body;
            }

            return [.. declared.Select((name, i) =>
                new SourceColumn(name, body is not null && i < body.Count ? body[i].DataType : null, cte.Name))];
        }
        finally
        {
            visiting.Remove(cte);
        }
    }

    // What a query outputs: its first branch's list (that is the one that
    // names a set operation's columns), stars spelled out through the
    // branch's sources. Unnamed expressions are skipped rather than guessed.
    private List<SourceColumn>? OutputOf(Snapshot snapshot, SqlQuery query, string owner, HashSet<SqlCte> visiting)
    {
        if (query.IsOpaque || query.Branches.Count == 0)
        {
            return null;
        }

        var branch = query.Branches[0];
        var columns = new List<SourceColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in branch.Output)
        {
            if (item.IsStar)
            {
                var covered = item.StarQualifier is { } qualifier
                    ? branch.Sources.Where(s => s.Label == qualifier)
                    : branch.Sources;
                foreach (var source in covered)
                {
                    foreach (var column in ColumnsOf(snapshot, branch, source, visiting) ?? [])
                    {
                        if (seen.Add(column.Name))
                        {
                            columns.Add(column with { Owner = owner });
                        }
                    }
                }

                continue;
            }

            if (item.Name is { } name && seen.Add(name))
            {
                columns.Add(new SourceColumn(name, ReferencedType(snapshot, branch, item, visiting), owner));
            }
        }

        return columns;
    }

    // The type of the column a plain reference item passes through, when the
    // branch's sources say what it is.
    private string? ReferencedType(Snapshot snapshot, SqlBlock branch, SqlOutputItem item, HashSet<SqlCte> visiting)
    {
        if (item.RefColumn is not { } column)
        {
            return null;
        }

        var candidates = item.RefQualifier is { } qualifier
            ? branch.Sources.Where(s => s.Label == qualifier)
            : branch.Sources;
        foreach (var source in candidates)
        {
            if (ColumnsOf(snapshot, branch, source, visiting)?.FirstOrDefault(c => c.Name == column) is { } found)
            {
                return found.DataType;
            }
        }

        return null;
    }

    /// <summary>
    /// Expands the <c>*</c> / <c>alias.*</c> in the select list of the
    /// statement under the caret into explicit columns — CTEs resolve through
    /// their derived output columns, everything else through the catalog.
    /// Null, with a one-line <paramref name="refusal"/> for the status line,
    /// when there's no star to expand or the expansion could change the result.
    /// </summary>
    public SqlCompletionContext.StarExpansion? ExpandSelectStar(string sql, int caret, out string? refusal)
    {
        var snapshot = _snapshot;
        var ctes = SqlScriptSplitter.StatementSpanAt(sql, caret) is { } span
            ? SqlCompletionContext.ExtractCteDefinitions(sql[span.Start..span.End])
            : [];
        return SqlCompletionContext.ExpandSelectStar(sql, caret, (schema, table) =>
        {
            if (schema.Length == 0 && ctes.Any(c => c.Name == table))
            {
                return CteColumns(snapshot, table, ctes)?.Select(c => c.Name).ToList();
            }

            return Resolve(snapshot, schema, table)?.Columns.Select(c => c.Column).ToList();
        }, out refusal);
    }

    // The columns `name` exposes when it names one of the statement's CTEs:
    // the derived output columns, plus the columns of the sources its stars
    // cover — each itself a CTE (recursively; a self-reference in a RECURSIVE
    // body is cut by the visited set) or a catalog table. Null when `name`
    // names no CTE at all.
    private List<SourceColumn>? CteColumns(Snapshot snapshot, string name, IReadOnlyList<SqlCompletionContext.CteDefinition> ctes)
    {
        var columns = new List<SourceColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return AddCteColumns(snapshot, name, ctes, columns, seen, visited) ? columns : null;
    }

    private bool AddCteColumns(
        Snapshot snapshot,
        string name,
        IReadOnlyList<SqlCompletionContext.CteDefinition> ctes,
        List<SourceColumn> columns,
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
            if (candidate.Name == name)
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
                columns.Add(new SourceColumn(column, null, cte.Name));
            }
        }

        foreach (var source in cte.StarSources)
        {
            if (source.Schema.Length == 0 && AddCteColumns(snapshot, source.Table, ctes, columns, seen, visited))
            {
                continue;
            }

            if (Resolve(snapshot, source.Schema, source.Table) is not { } table)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                if (seen.Add(column.Column))
                {
                    columns.Add(new SourceColumn(column.Column, column.DataType, table.Name));
                }
            }
        }

        return true;
    }

    // The relation a (schema, table) reference names, the way the server would
    // find it: exactly, when schema-qualified; otherwise the first schema on the
    // search_path that has it. When the path is unknown, only a name exactly one
    // schema has resolves — picking one of two same-named tables would be a
    // guess. An excluded schema on the path ends the lookup: completion can't
    // see what it holds, so it can't know the name isn't there.
    private CompletionTable? Resolve(Snapshot snapshot, string schema, string table)
    {
        if (schema.Length > 0)
        {
            return snapshot.TablesByKey.GetValueOrDefault((schema, table));
        }

        if (snapshot.SearchPath is { } path && !SessionSearchPathChanged)
        {
            foreach (var candidate in path)
            {
                if (snapshot.Excluded.Contains(candidate))
                {
                    return null;
                }

                if (snapshot.TablesByKey.TryGetValue((candidate, table), out var found))
                {
                    return found;
                }
            }

            return null;
        }

        return snapshot.TablesByName.TryGetValue(table, out var sameName) && sameName.Count == 1 ? sameName[0] : null;
    }

    private static List<SqlCompletionData> ColumnItems(IEnumerable<SourceColumn> columns) =>
        [.. columns.Select(c => ColumnItem(c.Name, c.DataType, c.Owner, CurrentColumnPriority))];

    // The data type rides in Detail (right-aligned in the row); the tooltip
    // names the owning relation, which the row itself doesn't show.
    private static SqlCompletionData ColumnItem(string column, string? dataType, string owner, double priority) =>
        new(column, SqlCompletionKind.Column, SqlIdentifier.QuoteIfNeeded(column), priority)
        {
            Detail = dataType,
            DescriptionText = $"column · {owner}",
        };

    // Collapse duplicate candidates, keeping the first — which, because callers
    // prepend the higher-priority items, is the better-ranked one.
    private static IReadOnlyList<SqlCompletionData> Dedupe(IEnumerable<SqlCompletionData> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SqlCompletionData>();
        foreach (var item in items)
        {
            if (seen.Add(KeyOf(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    // The per-caret items (`head`, a few dozen) deduplicated and put in front
    // of one of the snapshot's lists (`tail`, already unique, possibly a
    // hundred thousand rows), skipping only the tail rows a head item already
    // stands for. Same result as Dedupe(head ++ tail), without regrouping the
    // whole catalog on every popup open — that regrouping was most of the
    // cost of opening the list over a million-column catalog.
    private static IReadOnlyList<SqlCompletionData> Merge(List<SqlCompletionData> head, IReadOnlyList<SqlCompletionData> tail)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SqlCompletionData>(head.Count + tail.Count);
        foreach (var item in head)
        {
            if (seen.Add(KeyOf(item)))
            {
                result.Add(item);
            }
        }

        foreach (var item in tail)
        {
            if (seen.Count == 0 || !seen.Contains(KeyOf(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    // DedupeKey, computed once per item: snapshot items are reused by every
    // popup, so their keys are too.
    private static string KeyOf(SqlCompletionData item) => item.DedupeKey ??= DedupeKey(item);

    // What makes two rows the same candidate. A label alone isn't it: public.users
    // and audit.users are two tables, u.id and o.id two columns, and two
    // schemas' same-named functions two callables. Same-named catalog columns
    // (bare inserts) are one candidate, as are keywords typed in any case.
    private static string DedupeKey(SqlCompletionData item) => item.Kind switch
    {
        SqlCompletionKind.Table or SqlCompletionKind.Function => $"{(int)item.Kind}{item.Text}{item.Detail}",
        SqlCompletionKind.Column => $"{(int)item.Kind}{item.InsertText}",
        SqlCompletionKind.Keyword => $"{(int)item.Kind}{item.Text.ToUpperInvariant()}",
        _ => $"{(int)item.Kind}{item.Text}",
    };

    private sealed record Snapshot(
        IReadOnlyList<CompletionTable> Tables,
        IReadOnlyDictionary<(string, string), CompletionTable> TablesByKey,
        IReadOnlyDictionary<string, List<CompletionTable>> TablesByName,
        IReadOnlyList<SqlCompletionData> CallableItems,
        IReadOnlyList<SqlCompletionData> ProcedureItems,
        IReadOnlyList<ForeignKeyInfo> ForeignKeys,
        IReadOnlyList<string>? SearchPath,
        IReadOnlySet<string> Excluded,
        IReadOnlyList<SqlCompletionData> BaseItems,
        IReadOnlyList<SqlCompletionData> TableRefItems,
        IReadOnlyList<SqlCompletionData> PredicateBaseItems,
        IReadOnlyDictionary<string, List<CompletionFunction>> HintFunctions,
        IReadOnlyList<SqlCompletionData> TypeItems);
}
