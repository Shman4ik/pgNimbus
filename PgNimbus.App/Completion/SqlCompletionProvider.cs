using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

namespace PgNimbus.App.Completion;

/// <summary>A relation the completion catalog knows, with its columns in ordinal order.</summary>
public sealed record CompletionTable(string Schema, string Name, IReadOnlyList<TableColumn> Columns)
{
    /// <summary>pg_class.relkind: <c>r</c> table, <c>v</c> view, <c>m</c> materialized view, <c>p</c> partitioned table, <c>f</c> foreign table.</summary>
    public char Kind { get; init; } = 'r';

    /// <summary>A partition of another relation: reached through its parent, not offered on its own.</summary>
    public bool IsPartition { get; init; }

    /// <summary>The relation's comment, if it has one.</summary>
    public string? Comment { get; init; }

    /// <summary>The planner's row estimate; null before the relation was ever analyzed.</summary>
    public long? RowEstimate { get; init; }
}

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

    /// <summary>The sequences a <c>nextval('…')</c> can name.</summary>
    public IReadOnlyList<SequenceName> Sequences { get; init; } = [];

    /// <summary>The roles a GRANT … TO can name.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The server's settings, for SET and SHOW.</summary>
    public IReadOnlyList<SettingInfo> Settings { get; init; } = [];

    /// <summary>The extensions the server has, installed or available.</summary>
    public IReadOnlyList<ExtensionInfo> Extensions { get; init; } = [];
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

    // The only keywords a relation's position can hold (FROM |, JOIN |).
    private static readonly string[] TablePositionKeywords = ["LATERAL", "ONLY", "ROWS"];

    // The everyday functions, ranked a little over the rest of the curated list.
    private static readonly HashSet<string> CommonFunctions = new(StringComparer.Ordinal)
    {
        "count", "sum", "avg", "min", "max", "coalesce", "now", "date_trunc", "lower", "upper", "length",
        "round", "string_agg", "array_agg", "row_number", "to_char", "jsonb_build_object", "nullif", "extract",
        "pg_size_pretty", "pg_total_relation_size", "pg_terminate_backend", "pg_cancel_backend",
    };

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
        // the everyday administration ones
        "pg_size_pretty", "pg_total_relation_size", "pg_relation_size", "pg_table_size", "pg_indexes_size",
        "pg_database_size", "pg_terminate_backend", "pg_cancel_backend", "pg_sleep", "pg_get_viewdef",
        "pg_get_functiondef", "pg_get_indexdef", "pg_get_constraintdef", "current_setting", "set_config",
        "current_database", "current_schemas", "version", "regexp_matches", "regexp_split_to_table",
        "to_number", "clock_timestamp", "statement_timestamp", "pg_backend_pid", "txid_current",
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
    // A relation the statement already joins, after "schema." in a JOIN.
    private const double JoinedTablePriority = 5;
    // An ON condition's column tied by a foreign key to the other side, over
    // the relation's other columns — more when the statement doesn't use it yet.
    private const double ForeignKeyBoost = 10;
    private const double UsedForeignKeyBoost = 5;
    private const double CtePriority = 20;
    // A relation its bare name finds along the search_path, over a same-named
    // one in another schema.
    private const double PathTablePriority = 12;
    private const double TablePriority = 10;
    // Keywords that can begin an expression, where one is being started:
    // under the statement's own columns, over the catalog.
    private const double OperandKeywordPriority = 40;
    // The everyday functions (count, sum, now …) a little over the rest.
    private const double CommonFunctionPriority = 3.5;
    private const double FunctionPriority = 3;
    // A catalog-wide column the statement has no source for: a guess, under
    // the functions (docs/design/sql-completion-audit-2.md §6.2 step 3).
    private const double ColumnPriority = 2;
    // A table named in an expression (only ever as a qualifier there).
    private const double GeneralTablePriority = 1.5;
    private const double SchemaPriority = 1;
    // A partition, after its schema's "."; under its parent.
    private const double PartitionPriority = 0.5;
    // The system catalogs' relations and columns (E01), under the user's own;
    // the ones people query every day a little over the rest of them.
    private const double SystemTablePriority = 4;
    private const double CommonSystemTablePriority = 5;
    private const double RareInformationSchemaPriority = 0.8;
    private const double SystemGeneralTablePriority = 1.2;
    private const double SystemColumnPriority = 1.8;
    // pg_catalog's functions beyond the curated list (E02): under those, over
    // the catalog-wide column guesses.
    private const double BuiltinFunctionPriority = 2.5;
    // A value the column on the other side of a comparison takes (E07): an
    // enum's labels, TRUE/FALSE for a boolean — above the statement's columns.
    private const double ValuePriority = 150;
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

    /// <summary>
    /// Reads everything completion knows from the server — the read
    /// <see cref="RefreshAsync"/> publishes. Public so the measuring tools
    /// (<c>tools/CompletionBench</c>) snapshot a catalog the same way the
    /// editor does rather than through a copy of this method.
    /// </summary>
    public static async Task<CompletionCatalog> ReadCatalogAsync(SchemaService schemaService, IReadOnlySet<string> excluded, CancellationToken ct)
    {
        var schemaNames = new List<string>();
        var tables = new List<CompletionTable>();
        var functions = new List<CompletionFunction>();

        async Task AddRelationsAsync(string schema)
        {
            var relations = await schemaService.GetCompletionRelationsAsync(schema, ct);
            var columns = (await schemaService.GetAllColumnsAsync(schema, ct))
                .GroupBy(c => c.Table, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<TableColumn>)[.. g], StringComparer.Ordinal);
            foreach (var relation in relations)
            {
                tables.Add(new CompletionTable(schema, relation.Name, columns.GetValueOrDefault(relation.Name) ?? [])
                {
                    Kind = relation.Kind,
                    IsPartition = relation.IsPartition,
                    Comment = relation.Comment,
                    RowEstimate = relation.RowEstimate,
                });
            }
        }

        foreach (var schema in await schemaService.GetSchemasAsync(ct))
        {
            // An excluded schema contributes nothing anywhere, and its catalog
            // queries are skipped with it.
            if (excluded.Contains(schema.Name))
            {
                continue;
            }

            schemaNames.Add(schema.Name);
            await AddRelationsAsync(schema.Name);
            foreach (var function in await schemaService.GetFunctionsAsync(schema.Name, ct))
            {
                functions.Add(new CompletionFunction(schema.Name, function));
            }
        }

        // The system catalogs (E01): pg_stat_activity, pg_class,
        // information_schema.columns are everyday names for a Postgres client.
        // Their functions are pg_catalog's own, read below.
        foreach (var system in SystemSchemas)
        {
            if (!excluded.Contains(system))
            {
                schemaNames.Add(system);
                await AddRelationsAsync(system);
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
            Sequences = [.. (await ReadOptionalAsync(() => schemaService.GetSequenceNamesAsync(ct), [])).Where(s => !excluded.Contains(s.Schema))],
            Roles = [.. (await ReadOptionalAsync(() => schemaService.GetRolesAsync(ct), [])).Select(r => r.Name)],
            Settings = await ReadOptionalAsync(() => schemaService.GetSettingsAsync(ct), []),
            Extensions = await ReadOptionalAsync(() => schemaService.GetExtensionsAsync(ct), []),
        };
    }

    // A read the rest of completion does without: a server that refuses it
    // (a restricted role, a Postgres-compatible engine missing the view)
    // costs those candidates, not the whole catalog.
    private static async Task<IReadOnlyList<T>> ReadOptionalAsync<T>(Func<Task<IReadOnlyList<T>>> read, IReadOnlyList<T> fallback)
    {
        try
        {
            return await read();
        }
        catch (Npgsql.PostgresException)
        {
            return fallback;
        }
    }

    /// <summary>The system schemas completion reads relations from: pg_catalog (searched first, whatever the path says) and information_schema (reached qualified).</summary>
    public static readonly IReadOnlyList<string> SystemSchemas = ["pg_catalog", "information_schema"];

    private static bool IsSystemSchema(string schema) => schema is "pg_catalog" or "information_schema";

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
        var builtinOverloads = catalog.BuiltinFunctions
            .Where(f => !f.Function.IsInternal && f.Function.Kind != 'p')
            .GroupBy(f => f.Function.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FunctionInfo>)[.. g.Select(f => f.Function)], StringComparer.Ordinal);
        // The everyday functions, curated; with the catalog read, their
        // signatures and description go in the tooltip.
        var builtinFunctionItems = Functions
            .Select(f =>
            {
                builtinOverloads.TryGetValue(f, out var overloads);
                var (insert, caretIndex) = CallInsert(f, overloads);
                return new SqlCompletionData(f, SqlCompletionKind.Function, insert, CommonFunctions.Contains(f) ? CommonFunctionPriority : FunctionPriority)
                {
                    DescriptionText = overloads is null ? null : FunctionDescription(overloads),
                    CaretIndex = caretIndex,
                };
            })
            .ToList();
        // And the rest of pg_catalog's (E02): pg_size_pretty, to_timestamp,
        // pg_terminate_backend … — one row per name, what the catalog marks as
        // machinery left out, under the curated ones.
        var curatedNames = new HashSet<string>(Functions, StringComparer.Ordinal);
        foreach (var (name, overloads) in builtinOverloads.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            if (!curatedNames.Contains(name))
            {
                // A thousand rarely-typed names: one of them is often the only
                // longer match for a word typed in full (query → querytree), so,
                // like a catalog-wide column, Enter takes one only when chosen.
                builtinFunctionItems.Add(FunctionItem("pg_catalog", name, overloads, searchPath, BuiltinFunctionPriority, isGuess: true));
            }
        }

        // A name pg_catalog already has is what an unqualified call reaches
        // (pg_catalog is searched first, whatever the path says), so an
        // on-path schema's same-named function — pgcrypto's gen_random_uuid in
        // public — is one row too many (E04). Off the path it stays: it is
        // inserted qualified and reaches something else.
        var builtinNames = new HashSet<string>(Functions, StringComparer.Ordinal);
        foreach (var builtin in catalog.BuiltinFunctions)
        {
            if (!builtin.Function.IsInternal)
            {
                builtinNames.Add(builtin.Function.Name);
            }
        }

        // Overloads collapse into one row per schema-qualified name, with every
        // signature in the tooltip; procedures are kept apart because they are
        // only callable after CALL, never inside an expression. A function the
        // catalog marks as machinery (FunctionInfo.IsInternal: a type's I/O, an
        // operator's implementation …) is never a candidate.
        var callableItems = new List<SqlCompletionData>();
        var procedureItems = new List<SqlCompletionData>();
        foreach (var group in functions
            .Where(f => !f.Function.IsInternal)
            .Where(f => !(builtinNames.Contains(f.Function.Name) && (searchPath?.Contains(f.Schema) ?? f.Schema == "public")))
            .GroupBy(f => (f.Schema, f.Function.Name, IsProcedure: f.Function.Kind == 'p')))
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

        var tableRefItemsUnknownPath = new List<SqlCompletionData>(tableRefItems);
        foreach (var table in tables)
        {
            // Elsewhere a table completes to its bare name. In table position
            // (after FROM/JOIN) it completes bare when the bare name finds this
            // very relation — "customers" for public.customers on the default
            // path — and schema-qualified otherwise, so what gets written always
            // means the relation that was picked (sql-completion-audit-2.md F01).
            // The second list is for a session whose search_path the catalog
            // can't know (SessionSearchPathChanged).
            // A partition is reached through its parent: offered only after
            // "schema.", below everything else there (E05).
            // information_schema's _pg_* views are its own plumbing.
            if (table.IsPartition || (table.Schema == "information_schema" && table.Name.StartsWith("_pg_", StringComparison.Ordinal)))
            {
                continue;
            }

            // A system relation ranks under the user's own everywhere: the
            // prefix "ta" should reach a tasks table before information_schema.tables.
            var system = IsSystemSchema(table.Schema);
            baseItems.Add(TableItem(table, qualified: false, system ? SystemGeneralTablePriority : GeneralTablePriority));
            tableRefItems.Add(TableRefItem(table,
                ResolveShort(tablesByKey, tablesByName, excluded, searchPath, table.Name) == table));
            tableRefItemsUnknownPath.Add(TableRefItem(table,
                ResolveShort(tablesByKey, tablesByName, excluded, null, table.Name) == table));
            foreach (var column in table.Columns)
            {
                // A catalog-wide column is a guess: the statement names no
                // relation that has it (yet). Enter won't take one unasked.
                baseItems.Add(ColumnItem(new SourceColumn(column.Column, column.DataType, table.Name) { Facts = column },
                    system ? SystemColumnPriority : ColumnPriority, isGuess: true));
            }
        }

        // In table position only a relation or one of these can be written.
        var tableKeywordItems = TablePositionKeywords.Select(k => new SqlCompletionData(k, SqlCompletionKind.Keyword)).ToList();
        tableRefItems.AddRange(tableKeywordItems);
        tableRefItemsUnknownPath.AddRange(tableKeywordItems);
        baseItems.AddRange(callableItems);

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

        // What each callable name is, for the keywords after its call (only
        // OVER after a window function; FILTER / OVER after an aggregate).
        var callKinds = new Dictionary<string, char>(StringComparer.Ordinal);
        foreach (var function in catalog.BuiltinFunctions.Concat(functions))
        {
            if (function.Function.Kind is not ('f' or 'a' or 'w'))
            {
                continue;
            }

            callKinds[function.Function.Name] = callKinds.TryGetValue(function.Function.Name, out var seen)
                ? CombineCallKinds(seen, function.Function.Kind)
                : function.Function.Kind;
        }

        var typeItems = catalog.Types.Count == 0
            ? FallbackTypeItems
            : Dedupe(catalog.Types.Where(t => !excluded.Contains(t.Schema)).Select(t => TypeItem(t, searchPath)));

        // An enum's labels by the name format_type gives its columns, which
        // is what a column's DataType holds (E07).
        var enumLabels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var type in catalog.Types)
        {
            if (type.EnumLabels.Count > 0 && !excluded.Contains(type.Schema))
            {
                enumLabels[type.DisplayName] = type.EnumLabels;
            }
        }

        // What each foreign-key column references, for its tooltip: "→ users.id".
        var references = new Dictionary<(string, string, string), string>();
        foreach (var fk in foreignKeys)
        {
            for (var i = 0; i < fk.FromColumns.Count && i < fk.ToColumns.Count; i++)
            {
                references.TryAdd((fk.FromSchema, fk.FromTable, fk.FromColumns[i]), $"{fk.ToTable}.{fk.ToColumns[i]}");
            }
        }

        var sequences = catalog.Sequences.Where(s => !excluded.Contains(s.Schema)).ToList();

        return new Snapshot(
            tables,
            tablesByKey,
            tablesByName,
            callableItems,
            procedureItems,
            foreignKeys,
            searchPath,
            new HashSet<string>(excluded, StringComparer.Ordinal),
            new CandidateList(Dedupe(baseItems)),
            new CandidateList(Dedupe(baseItems.Where(i => i.Kind != SqlCompletionKind.Keyword))),
            new CandidateList(Dedupe(tableRefItems)),
            new CandidateList(Dedupe(tableRefItemsUnknownPath)),
            new CandidateList(Dedupe(predicateBase)),
            new CandidateList(Dedupe(predicateBase.Where(i => i.Kind != SqlCompletionKind.Keyword))),
            hintFunctions,
            typeItems,
            callKinds)
        {
            EnumLabels = enumLabels,
            References = references,
            Sequences = sequences,
            Catalog = catalog,
        };
    }

    // One name, two overloads: a window function only when every overload is
    // one (rank is also an aggregate, WITHIN GROUP); any aggregate or window
    // overload makes FILTER/OVER possible.
    private static char CombineCallKinds(char a, char b) =>
        a == b ? a : a is 'a' or 'w' || b is 'a' or 'w' ? 'a' : 'f';

    // The call kinds of the everyday functions, for a catalog not read yet.
    private static readonly Dictionary<string, char> FallbackCallKinds = new(StringComparer.Ordinal)
    {
        ["row_number"] = 'w', ["rank"] = 'w', ["dense_rank"] = 'w', ["percent_rank"] = 'w', ["cume_dist"] = 'w',
        ["ntile"] = 'w', ["lag"] = 'w', ["lead"] = 'w', ["first_value"] = 'w', ["last_value"] = 'w', ["nth_value"] = 'w',
        ["count"] = 'a', ["sum"] = 'a', ["avg"] = 'a', ["min"] = 'a', ["max"] = 'a', ["array_agg"] = 'a',
        ["string_agg"] = 'a', ["json_agg"] = 'a', ["jsonb_agg"] = 'a', ["bool_and"] = 'a', ["bool_or"] = 'a',
    };

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
            return SqlCallSite.ValuesRowAt(sql, caret) is { } row ? ValuesRowHint(sql, caret, row) : null;
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

    // Inside a row of INSERT … VALUES: the columns the row fills, the one the
    // caret's value goes into marked, like a call's parameter (§6.4).
    private (SqlCallSite Site, IReadOnlyList<SignatureHint> Hints)? ValuesRowHint(string sql, int caret, SqlCallSite row)
    {
        var snapshot = _snapshot;
        var (start, end) = SqlCompletionContext.CompletionStatementSpan(sql, caret);
        var statement = sql[start..end];
        var scope = Scope.At(statement, caret - start);
        // The VALUES list is the INSERT's source query: its own block, owned by the INSERT's.
        var insert = scope.Block is { Kind: SqlBlockKind.Insert } own ? own : scope.Block?.Query.Owner;
        if (scope.Unknown || insert is not { Kind: SqlBlockKind.Insert, Target: { } target }
            || ColumnsOf(snapshot, insert, target, []) is not { Count: > 0 } columns)
        {
            return null;
        }

        // The listed columns in their order, else every column of the target.
        var byName = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var named = insert.InsertColumns is { } list
            ? SqlLexer.Tokenize(statement, list.Start, list.End)
                .Where(t => t.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
                .Select(t => SqlLexer.IdentifierName(statement, t))
                .Select(n => byName.TryGetValue(n, out var c) ? c : new SourceColumn(n, null, target.Label))
                .ToList()
            : [.. columns];
        var parameters = named
            .Select(c => new SqlParameter($"{SqlIdentifier.QuoteIfNeeded(c.Name)} {c.DataType}".TrimEnd(), c.Name, false, c.Facts?.HasDefault == true))
            .ToList();
        var active = row.ArgumentIndex < parameters.Count ? row.ArgumentIndex : -1;
        return (row, [new SignatureHint(target.Schema, "VALUES", parameters, active, "")]);
    }

    // A catalog callable (all overloads of one schema.name): inserts as
    // "name()" — schema-qualified when the schema isn't on the search_path, so
    // the call resolves to the function that was picked — with every signature
    // in the tooltip, doubling as a lightweight parameter hint.
    private static SqlCompletionData FunctionItem(
        string schema, string name, IReadOnlyList<FunctionInfo> overloads, IReadOnlyList<string>? searchPath, double priority = FunctionPriority,
        bool isGuess = false)
    {
        // pg_catalog is searched first whatever the path says: its functions never need the schema.
        var onPath = schema == "pg_catalog" || (searchPath?.Contains(schema) ?? schema == "public");
        var callName = onPath
            ? SqlIdentifier.QuoteIfNeeded(name)
            : $"{SqlIdentifier.QuoteIfNeeded(schema)}.{SqlIdentifier.QuoteIfNeeded(name)}";
        var (insert, caretIndex) = CallInsert(callName, overloads);
        return new SqlCompletionData(name, SqlCompletionKind.Function, insert, priority)
        {
            Detail = schema,
            DescriptionText = FunctionDescription(overloads),
            IsGuess = isGuess,
            CaretIndex = caretIndex,
        };
    }

    // What accepting a callable writes, and where the caret goes: "name()"
    // with the caret between the parens — or, for a function that is only a
    // window function, the call with its window (§6.4, F03): "row_number()
    // OVER (|)", "lag(|) OVER ()" when it takes arguments. A null index
    // means CompletionEdits' own default, inside the call's parens.
    private static (string Insert, int? CaretIndex) CallInsert(string name, IReadOnlyList<FunctionInfo>? overloads)
    {
        var windowOnly = overloads is { Count: > 0 } ? overloads.All(o => o.Kind == 'w') : FallbackCallKinds.GetValueOrDefault(name) == 'w';
        if (!windowOnly)
        {
            return ($"{name}()", null);
        }

        var takesArguments = overloads is { Count: > 0 } ? overloads.Any(o => o.Arguments.Length > 0) : FallbackWindowsWithArguments.Contains(name);
        var insert = $"{name}() OVER ()";
        return (insert, takesArguments ? name.Length + 1 : insert.Length - 1);
    }

    private static readonly HashSet<string> FallbackWindowsWithArguments = new(StringComparer.Ordinal)
    {
        "ntile", "lag", "lead", "first_value", "last_value", "nth_value",
    };

    // Every signature, with its DEFAULTs (E06), then the function's comment.
    private static string FunctionDescription(IReadOnlyList<FunctionInfo> overloads)
    {
        var lines = overloads
            .Select(f => $"{FunctionSignatureFormatter.KindLabel(f)} · {FunctionSignatureFormatter.Format(f.FullArguments is { } full ? f with { Arguments = full } : f)}")
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
        if (overloads.Count > 8)
        {
            lines.Add($"… {overloads.Count - 8} more");
        }

        if (overloads.Select(f => f.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) is { } description)
        {
            lines.Add(description);
        }

        return string.Join("\n", lines);
    }

    // A relation in table position: bare when the bare name resolves to it
    // (and ranked above same-named relations elsewhere, which is what keeps
    // "UPDATE customers" on public.customers), schema-qualified otherwise.
    private static SqlCompletionData TableRefItem(CompletionTable table, bool resolvesBare) =>
        TableItem(table, qualified: !resolvesBare,
            CommonSystemRank.TryGetValue($"{table.Schema}.{table.Name}", out var rank) ? CommonSystemTablePriority - (rank * 0.01)
            // information_schema's rarer views (sql_features, …) under its schema row.
            : table.Schema == "information_schema" ? RareInformationSchemaPriority
            : IsSystemSchema(table.Schema) ? SystemTablePriority
            : resolvesBare ? PathTablePriority : TablePriority);

    // The system relations a Postgres client queries every day, most queried first.
    private static readonly string[] CommonSystemRelations =
    [
        "pg_catalog.pg_stat_activity", "pg_catalog.pg_class", "pg_catalog.pg_namespace", "pg_catalog.pg_locks",
        "pg_catalog.pg_stat_user_tables", "pg_catalog.pg_indexes", "pg_catalog.pg_tables", "pg_catalog.pg_views",
        "pg_catalog.pg_settings", "pg_catalog.pg_roles", "pg_catalog.pg_database", "pg_catalog.pg_attribute",
        "pg_catalog.pg_constraint", "pg_catalog.pg_proc", "pg_catalog.pg_type", "pg_catalog.pg_extension",
        "pg_catalog.pg_stat_statements", "pg_catalog.pg_stat_user_indexes", "pg_catalog.pg_stat_database",
        "pg_catalog.pg_matviews", "pg_catalog.pg_sequences", "pg_catalog.pg_stat_replication", "pg_catalog.pg_user",
        "information_schema.columns", "information_schema.tables", "information_schema.views",
        "information_schema.routines", "information_schema.schemata", "information_schema.table_constraints",
        "information_schema.key_column_usage", "information_schema.referential_constraints",
        "information_schema.parameters", "information_schema.table_privileges",
    ];

    private static readonly Dictionary<string, int> CommonSystemRank =
        CommonSystemRelations.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);

    // A catalog relation, its kind, size and comment in the tooltip (E06).
    private static SqlCompletionData TableItem(CompletionTable table, bool qualified, double priority)
    {
        var item = TableItem(table.Schema, table.Name, qualified, priority);
        return new SqlCompletionData(item.Text, item.Kind, item.InsertText, item.Priority)
        {
            AliasTable = item.AliasTable,
            Detail = item.Detail,
            DescriptionText = RelationDescription(table),
        };
    }

    // "view · commerce · ~12,000 rows", the comment on the next line.
    private static string RelationDescription(CompletionTable table)
    {
        var kind = table.Kind switch
        {
            'v' => "view",
            'm' => "materialized view",
            'p' => "partitioned table",
            'f' => "foreign table",
            _ => table.IsPartition ? "partition" : "table",
        };
        var line = $"{kind} · {table.Schema}";
        if (table.RowEstimate is { } rows && table.Kind is 'r' or 'm' or 'p')
        {
            line += $" · ~{rows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows";
        }

        return table.Comment is { Length: > 0 } comment ? $"{line}\n{comment}" : line;
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
        var slot = SqlValueSlot.At(statement, caret);
        if (context.InStringOrComment)
        {
            // Prose, except a string that holds a value the text around it
            // decides: an enum column's label, a sequence in nextval('…') (E07).
            return slot is { InString: true } ? StringValueItems(snapshot, statement, caret, slot) : [];
        }

        var items = Candidates(snapshot, statement, caret, context, slot);
        if (slot is not null && ValueItems(snapshot, statement, caret, slot) is { Count: > 0 } values)
        {
            items = WithValues(values, items);
        }

        // Right after a star in the select list: the star spelled out, as the
        // palette's Expand * does it (§6.4) — declining where that would
        // change the result.
        if (caret > 0 && statement[caret - 1] == '*' && StarExpansionItem(sql, caretOffset) is { } expansion)
        {
            items = Prepend([expansion], items);
        }

        // Inside "…" the user is spelling a name; keywords can't go there.
        return context.InQuotedIdentifier
            ? [.. items.Where(i => i.Kind != SqlCompletionKind.Keyword)]
            : items;
    }

    private IReadOnlyList<SqlCompletionData> Candidates(Snapshot snapshot, string statement, int caret, SqlCompletionContext.CaretContext context, SqlValueSlot? slot)
    {
        if (IsTypePosition(statement, caret))
        {
            return snapshot.TypeItems;
        }

        // "extract(|": only a field can go there, then FROM.
        if (slot is { InString: false, Function: "extract", ArgumentIndex: 0 })
        {
            return ExtractFieldItems;
        }

        var scope = Scope.At(statement, caret);
        var chain = SqlCompletionContext.GetQualifierChainBeforeCaret(statement, caret);
        if (chain.Count > 0)
        {
            return GetMemberCompletions(snapshot, chain, statement, scope);
        }

        if (SqlCompletionContext.IsAfterKeyword(statement, caret, "call"))
        {
            return Dedupe(snapshot.ProcedureItems.Concat(TableRefItemsOf(snapshot).Where(i => i.Kind == SqlCompletionKind.Schema)));
        }

        if (scope.Block is { } contextBlock && ColumnListCompletions(snapshot, statement, contextBlock, caret) is { } columnList)
        {
            return columnList;
        }

        // Which keywords are legal here (sql-completion-audit-2.md §6.2 step 1):
        // where only keywords fit — a statement's start, right after a finished
        // expression, after IS / ORDER / INSERT … — they are the whole list;
        // where an expression starts, the ones that can start it join the
        // columns and functions; anywhere else the keywords stay as they are.
        var advice = context.InQuotedIdentifier ? SqlKeywordAdvice.None : SqlKeywordGrammar.At(statement, caret, snapshot.CallKindOf);
        if (advice.KeywordsOnly)
        {
            var keywords = WithWindowParens(KeywordItems(advice.Keywords, StatementKeywordPriority));
            if (advice.AfterInsertTarget && scope.Block is { Kind: SqlBlockKind.Insert, InsertColumns: null, Target: { } target } insert
                && ColumnsOf(snapshot, insert, target, []) is { Count: > 0 } targetColumns)
            {
                keywords.InsertRange(0, InsertColumnListItems(targetColumns));
            }

            return keywords;
        }

        // Where an expression starts, the keywords that can start one replace
        // every other keyword; they go in with the per-caret items, and the
        // catalog-wide tail is the one without keywords, so a million-row list
        // is still copied once.
        var operandKeywords = advice.Position == SqlKeywordPosition.Operand ? advice.Keywords : null;
        var items = ClauseCandidates(snapshot, statement, caret, context, scope, operandKeywords);
        return advice.Position == SqlKeywordPosition.Operand && ListSnippets(snapshot, statement, caret, scope) is { Count: > 0 } snippets
            ? Prepend(snippets, items)
            : items;
    }

    private IReadOnlyList<SqlCompletionData> ClauseCandidates(
        Snapshot snapshot, string statement, int caret, SqlCompletionContext.CaretContext context, Scope scope, IReadOnlyList<string>? operandKeywords)
    {
        return context.Clause switch
        {
            // A finished JOIN target owes ON/USING; a finished FROM item is
            // followed by a clause. No relation can come next in either (only
            // a comma brings one back), so the list is those words alone.
            SqlClause.JoinTableRef when SqlCompletionContext.IsAfterCompleteJoinTarget(statement, caret) => JoinKeywordBoostItems,
            SqlClause.TableRef or SqlClause.FromTableRef
                when SqlCompletionContext.IsAfterCompleteFromItem(statement, caret) => FromItemFollowItems,
            SqlClause.TableRef or SqlClause.FromTableRef => BuildTableRefCompletions(snapshot, statement, scope, boosted: []),
            SqlClause.JoinTableRef => BuildTableRefCompletions(snapshot, statement, scope, FkNeighborItems(snapshot, statement, scope)),
            SqlClause.Predicate when SqlCompletionContext.IsAfterOnKeyword(statement, caret) =>
                GetJoinConditionCompletions(snapshot, statement, caret, scope, operandKeywords),
            SqlClause.Predicate => GetPredicateCompletions(snapshot, statement, scope, operandKeywords),
            // A select list whose block already names its sources can only
            // reference those (and what is around it): not another branch's
            // tables, not the rest of the catalog.
            SqlClause.ColumnRef when scope.Block is { Kind: not SqlBlockKind.Values, Sources.Count: > 0 } =>
                GetPredicateCompletions(snapshot, statement, scope, operandKeywords),
            _ => GetGeneralCompletions(snapshot, statement, scope, operandKeywords),
        };
    }

    // The fields extract( takes, most used first.
    private static readonly string[] DateFields =
    [
        "year", "month", "day", "hour", "minute", "second", "epoch", "dow", "doy", "week", "quarter", "isodow",
        "isoyear", "milliseconds", "microseconds", "decade", "century", "millennium", "timezone", "julian",
    ];

    private static readonly IReadOnlyList<SqlCompletionData> ExtractFieldItems =
        [.. DateFields.Select((f, i) => new SqlCompletionData(f.ToUpperInvariant(), SqlCompletionKind.Keyword, f.ToUpperInvariant(), ValuePriority - (i * 0.01))
        {
            DescriptionText = "date/time field",
        })];

    // The units date_trunc('…') and date_bin take, in a string.
    private static readonly string[] TruncUnits =
    [
        "day", "month", "year", "hour", "week", "minute", "second", "quarter", "decade", "century", "millennium",
        "milliseconds", "microseconds",
    ];

    // Values the column on the left of a comparison takes, written as
    // literals: an enum's labels quoted, TRUE/FALSE for a boolean. Null when
    // the slot names no column the scope can resolve, or a type with no such list.
    private List<SqlCompletionData>? ValueItems(Snapshot snapshot, string statement, int caret, SqlValueSlot slot)
    {
        if (slot.ComparedColumn is not { } column || ComparedColumnType(snapshot, statement, caret, column) is not { } type)
        {
            return null;
        }

        if (type is "boolean")
        {
            return
            [
                new SqlCompletionData("TRUE", SqlCompletionKind.Keyword, "TRUE", ValuePriority) { DescriptionText = "boolean" },
                new SqlCompletionData("FALSE", SqlCompletionKind.Keyword, "FALSE", ValuePriority - 0.01) { DescriptionText = "boolean" },
            ];
        }

        return snapshot.EnumLabels.TryGetValue(type, out var labels)
            ? [.. labels.Select((label, i) => new SqlCompletionData(label, SqlCompletionKind.Value, $"'{label.Replace("'", "''", StringComparison.Ordinal)}'", ValuePriority - (i * 0.01))
            {
                Detail = type,
                DescriptionText = $"{type} label",
            })]
            : null;
    }

    // Inside a string that holds a value: the value alone, the quotes are
    // already there — an enum label, a sequence name, a date_trunc unit.
    private List<SqlCompletionData> StringValueItems(Snapshot snapshot, string statement, int caret, SqlValueSlot slot)
    {
        if (slot.ComparedColumn is { } column)
        {
            return ComparedColumnType(snapshot, statement, caret, column) is { } type && snapshot.EnumLabels.TryGetValue(type, out var labels)
                ? [.. labels.Select((label, i) => new SqlCompletionData(label, SqlCompletionKind.Value, label.Replace("'", "''", StringComparison.Ordinal), ValuePriority - (i * 0.01))
                {
                    Detail = type,
                    DescriptionText = $"{type} label",
                })]
                : [];
        }

        return (slot.Function, slot.ArgumentIndex) switch
        {
            ("nextval" or "currval" or "setval", 0) => [.. snapshot.Sequences.Select(s =>
            {
                var onPath = (SessionSearchPathChanged ? null : snapshot.SearchPath)?.Contains(s.Schema) ?? s.Schema == "public";
                return new SqlCompletionData(s.Name, SqlCompletionKind.Sequence,
                    onPath ? SqlIdentifier.QuoteIfNeeded(s.Name) : $"{SqlIdentifier.QuoteIfNeeded(s.Schema)}.{SqlIdentifier.QuoteIfNeeded(s.Name)}",
                    onPath ? ValuePriority : ValuePriority - 1)
                {
                    Detail = s.Schema,
                };
            })],
            ("date_trunc" or "date_bin", 0) => [.. TruncUnits.Select((u, i) => new SqlCompletionData(u, SqlCompletionKind.Value, u, ValuePriority - (i * 0.01)) { DescriptionText = "date/time unit" })],
            ("date_part", 0) => [.. DateFields.Select((u, i) => new SqlCompletionData(u, SqlCompletionKind.Value, u, ValuePriority - (i * 0.01)) { DescriptionText = "date/time field" })],
            _ => [],
        };
    }

    // The declared type (format_type's spelling) of the column a comparison
    // names: through the block's sources the way a qualifier resolves, the
    // target of an UPDATE/INSERT, or schema.table.column exactly.
    private string? ComparedColumnType(Snapshot snapshot, string statement, int caret, IReadOnlyList<string> column)
    {
        var name = column[^1];
        if (column.Count == 3)
        {
            return snapshot.TablesByKey.TryGetValue((column[0], column[1]), out var exact)
                ? exact.Columns.FirstOrDefault(c => c.Column == name)?.DataType
                : null;
        }

        var scope = Scope.At(statement, caret);
        if (scope.Unknown || scope.Block is not { } block)
        {
            return null;
        }

        var candidates = new List<SqlSource>();
        foreach (var level in SqlScopeModel.VisibleSources(block))
        {
            candidates.AddRange(column.Count == 2
                ? level.Where(s => s.Alias == column[0] || (s.Alias is null && s.Name == column[0]))
                : level);
        }

        if (block.Target is { } target && (column.Count == 1 || target.Label == column[0]))
        {
            candidates.Insert(0, target);
        }

        foreach (var source in candidates)
        {
            if (ColumnsOf(snapshot, block, source, [])?.FirstOrDefault(c => c.Name == name) is { DataType: { } type })
            {
                return type;
            }
        }

        return null;
    }

    // The values in front of the rest, the rest without the rows a value
    // stands for (TRUE is already an operand keyword there).
    private static IReadOnlyList<SqlCompletionData> WithValues(List<SqlCompletionData> values, IReadOnlyList<SqlCompletionData> items) =>
        Prepend(values, items);

    // `head` in front of `items`, without the keyword and value rows `head` already has.
    private static IReadOnlyList<SqlCompletionData> Prepend(List<SqlCompletionData> values, IReadOnlyList<SqlCompletionData> items)
    {
        var keys = new HashSet<string>(values.Select(KeyOf), StringComparer.Ordinal);
        var result = new List<SqlCompletionData>(values.Count + items.Count);
        result.AddRange(values);
        foreach (var item in items)
        {
            if (item.Kind is not (SqlCompletionKind.Keyword or SqlCompletionKind.Value) || !keys.Contains(KeyOf(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    // The row that replaces the select list's star(s) at `caret` with the
    // columns, when the star ends the list there and can be spelled out.
    private SqlCompletionData? StarExpansionItem(string sql, int caret)
    {
        if (ExpandSelectStar(sql, caret, out _) is not { } expansion || expansion.Start + expansion.Length != caret)
        {
            return null;
        }

        return new SqlCompletionData(expansion.Replacement, SqlCompletionKind.Snippet, expansion.Replacement, StatementKeywordPriority + 1)
        {
            ReplaceFrom = expansion.Start,
            Detail = "expand *",
            DescriptionText = "the select list with every star spelled out",
        };
    }

    // "(first_name, last_name, email) VALUES (|)" after INSERT INTO t (§6.4):
    // the columns a row can be written to (not a generated one, not an
    // identity GENERATED ALWAYS), the caret in the first value — and, when
    // they differ, the ones a row must be given (NOT NULL, no default).
    private static List<SqlCompletionData> InsertColumnListItems(IReadOnlyList<SourceColumn> columns)
    {
        var writable = columns.Where(c => c.Facts is not { IsGenerated: true } && c.Facts?.Identity != 'a').ToList();
        var required = writable.Where(c => c.Facts is { NotNull: true, HasDefault: false, Identity: '\0' }).ToList();
        var items = new List<SqlCompletionData>();
        foreach (var (list, what, priority) in new[] { (writable, "every column a row can be given", 1.0), (required, "the columns a row must be given", 0.5) })
        {
            if (list.Count == 0 || (list == required && required.Count == writable.Count))
            {
                continue;
            }

            var text = $"({string.Join(", ", list.Select(c => SqlIdentifier.QuoteIfNeeded(c.Name)))}) VALUES ()";
            items.Add(new SqlCompletionData(text, SqlCompletionKind.Snippet, text, StatementKeywordPriority + priority)
            {
                CaretIndex = text.Length - 1,
                Detail = "columns",
                DescriptionText = $"column list and VALUES · {what}",
            });
        }

        return items;
    }

    // Whole lists offered where an expression list starts (§6.4): after
    // SELECT, every column of the block's sources; after GROUP BY, the select
    // list's items that aren't aggregates.
    private List<SqlCompletionData>? ListSnippets(Snapshot snapshot, string statement, int caret, Scope scope)
    {
        if (scope.Unknown || scope.Block is not { Kind: SqlBlockKind.Select } block)
        {
            return null;
        }

        if (block.ClauseAt(caret) == "group" && SqlCompletionContext.IsAfterKeyword(statement, caret, "by")
            && GroupByItems(snapshot, statement, block) is { Count: > 0 } groupBy)
        {
            var text = string.Join(", ", groupBy);
            return [new SqlCompletionData(text, SqlCompletionKind.Snippet, text, CurrentColumnPriority + 10)
            {
                Detail = "non-aggregated",
                DescriptionText = "the select list's items that aren't aggregates",
            }];
        }

        if ((SqlCompletionContext.IsAfterKeyword(statement, caret, "select") || SqlCompletionContext.IsAfterKeyword(statement, caret, "distinct"))
            && block.Sources.Count > 0)
        {
            var parts = new List<string>();
            foreach (var source in block.Sources)
            {
                if (ColumnsOf(snapshot, block, source, []) is not { Count: > 0 } columns)
                {
                    return null; // a star it can't spell out: no list is better than a partial one
                }

                var qualify = block.Sources.Count > 1 || source.Alias is not null;
                parts.AddRange(columns.Select(c => qualify
                    ? $"{SqlIdentifier.QuoteIfNeeded(source.Label)}.{SqlIdentifier.QuoteIfNeeded(c.Name)}"
                    : SqlIdentifier.QuoteIfNeeded(c.Name)));
            }

            var text = string.Join(", ", parts);
            return [new SqlCompletionData(text, SqlCompletionKind.Snippet, text, OperandKeywordPriority - 0.005)
            {
                Detail = "all columns",
                DescriptionText = $"every column of {string.Join(", ", block.Sources.Select(s => s.Label))}",
            }];
        }

        return null;
    }

    // The select list's expressions a GROUP BY needs: the ones that aren't an
    // aggregate or window call (or a bare constant). Empty unless the list
    // has both kinds — with no aggregate there is nothing to group for.
    private static List<string> GroupByItems(Snapshot snapshot, string statement, SqlBlock block)
    {
        var plain = new List<string>();
        var aggregates = 0;
        foreach (var item in block.Output)
        {
            if (item.IsStar || item.Expression is not { } span || span.End > statement.Length)
            {
                return [];
            }

            var tokens = SqlLexer.Tokenize(statement, span.Start, span.End).Where(t => !t.IsTrivia).ToList();
            var aggregate = false;
            var names = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier))
                {
                    continue;
                }

                var word = SqlLexer.IdentifierName(statement, tokens[i]);
                names = true;
                if (word == "over" && tokens[i].Kind == SqlTokenKind.Word
                    || (i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.OpenParen && snapshot.CallKindOf(word) is 'a' or 'w'))
                {
                    aggregate = true;
                }
            }

            if (aggregate)
            {
                aggregates++;
            }
            else if (names)
            {
                plain.Add(statement[span.Start..span.End]);
            }
        }

        return aggregates > 0 ? plain : [];
    }

    // After a call, OVER and FILTER are written with their parentheses and
    // the caret inside: "OVER (|)", "FILTER (WHERE |)" (§6.4).
    private static List<SqlCompletionData> WithWindowParens(List<SqlCompletionData> keywords)
    {
        for (var i = 0; i < keywords.Count; i++)
        {
            var (insert, what) = keywords[i].Text switch
            {
                "OVER" => ("OVER ()", "window"),
                "FILTER" => ("FILTER (WHERE )", "aggregate filter"),
                _ => (null, null),
            };
            if (insert is not null)
            {
                keywords[i] = new SqlCompletionData(keywords[i].Text, SqlCompletionKind.Keyword, insert, keywords[i].Priority)
                {
                    CaretIndex = insert.Length - 1,
                    DescriptionText = what,
                };
            }
        }

        return keywords;
    }

    // The keywords `keywords`, ranked by their order: the first gets `top`.
    private static List<SqlCompletionData> KeywordItems(IReadOnlyList<string> keywords, double top) =>
        [.. keywords.Select((k, i) => new SqlCompletionData(k, SqlCompletionKind.Keyword, k, top - (i * 0.01)))];

    // The per-caret items plus, where an expression starts, the keywords that
    // can start one — ranked above the catalog, not above the statement's own
    // columns — merged in front of the catalog-wide `tail`, which then leaves
    // its own keywords out (IN, ORDER, WHEN can't start an expression).
    private static IReadOnlyList<SqlCompletionData> WithCatalog(
        List<SqlCompletionData> items, IReadOnlyList<string>? operandKeywords,
        CandidateList tail, CandidateList tailWithoutKeywords)
    {
        if (operandKeywords is null)
        {
            return Merge(items, tail);
        }

        items.AddRange(KeywordItems(operandKeywords, OperandKeywordPriority));
        return Merge(items, tailWithoutKeywords);
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
                ? ColumnItems(CatalogColumns(snapshot, exact))
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
                    var columns = ColumnItems(ColumnsOf(snapshot, block, source, []) ?? []);
                    return JoinConditionSides(snapshot, statement, block, source, scope.Caret) is { } sides
                        ? RankByForeignKeys(snapshot, statement, qualifier, columns, sides.Qualified, sides.Others)
                        : columns;
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
            return ColumnItems(CatalogColumns(snapshot, direct));
        }

        // schema. → the schema's tables and functions. After JOIN, the tables
        // an FK connects to the statement's first and the ones it already
        // joins last; a partition always last (it is reached through its parent).
        var items = new List<SqlCompletionData>();
        HashSet<(string, string)>? neighbours = null;
        HashSet<(string, string)>? joined = null;
        List<TableReference> statementTables = [];
        if (SqlCompletionContext.GetCaretContext(statement, scope.Caret).Clause == SqlClause.JoinTableRef)
        {
            statementTables = ResolvedReferences(snapshot, scope.Relations(statement, int.MaxValue, exceptAt: QualifiedNameStart(statement, scope.Caret)));
            neighbours = [.. ForeignKeyMatcher.FindJoinCandidates(statementTables, snapshot.ForeignKeys)];
            joined = [.. statementTables.Select(t => (t.Schema, t.Table))];
        }

        foreach (var table in snapshot.Tables)
        {
            if (table.Schema == qualifier && !(qualifier == "information_schema" && table.Name.StartsWith("_pg_", StringComparison.Ordinal)))
            {
                var key = (table.Schema, table.Name);
                var priority = table.IsPartition ? PartitionPriority
                    : neighbours?.Contains(key) == true ? FkTablePriority
                    : joined?.Contains(key) == true ? JoinedTablePriority
                    : CommonSystemRank.TryGetValue($"{table.Schema}.{table.Name}", out var rank) ? TablePriority + 1 - (rank * 0.01)
                    : TablePriority;
                items.Add(TableItem(table, qualified: false, priority));
                if (neighbours?.Contains(key) == true)
                {
                    // The "schema." is typed already: the table goes in bare.
                    items.AddRange(JoinSnippets(snapshot, statement, scope, statementTables, table.Schema, table.Name, SqlIdentifier.QuoteIfNeeded(table.Name), priority));
                }
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

    private static readonly IReadOnlyList<SqlCompletionData> JoinKeywordBoostItems =
    [
        new SqlCompletionData("ON", SqlCompletionKind.Keyword, "ON", JoinConditionPriority),
        new SqlCompletionData("USING", SqlCompletionKind.Keyword, "USING", JoinConditionPriority),
    ];

    // What can follow a finished FROM item, most common first: "FROM users u w"
    // should preselect WHERE, not WHEN/WITH, which can't go there at all.
    // Ranked by position like StatementStartItems, above the tables (which
    // can't follow an item either, only a comma can bring one back).
    // A keyword never written alone comes with the words that always follow it
    // (JOIN with its kind, BY), as one row that its initials find: "lj".
    private static readonly IReadOnlyList<SqlCompletionData> FromItemFollowItems =
        new[]
        {
            "WHERE", "JOIN", "LEFT JOIN", "INNER JOIN", "GROUP BY", "ORDER BY", "LIMIT", "CROSS JOIN",
            "RIGHT JOIN", "FULL JOIN", "LEFT OUTER JOIN", "NATURAL JOIN", "LEFT", "RIGHT", "FULL", "NATURAL",
            "HAVING", "OFFSET", "UNION", "EXCEPT", "INTERSECT",
        }
        .Select((keyword, index) => new SqlCompletionData(keyword, SqlCompletionKind.Keyword, keyword, StatementKeywordPriority - index))
        .ToList();

    // Table position (after FROM/INTO/UPDATE …): only what can be a table there —
    // the statement's CTEs first, then schemas + tables (+ keywords, so
    // "JOIN"/"WHERE" still complete after "FROM users "). No columns.
    private IReadOnlyList<SqlCompletionData> BuildTableRefCompletions(Snapshot snapshot, string statement, Scope scope, IEnumerable<SqlCompletionData> boosted)
    {
        var items = new List<SqlCompletionData>();
        foreach (var cte in scope.CteNames(statement))
        {
            items.Add(new SqlCompletionData(cte, SqlCompletionKind.Cte, SqlIdentifier.QuoteIfNeeded(cte), CtePriority));
        }

        items.AddRange(boosted);
        return Merge(items, TableRefItemsOf(snapshot));
    }

    // Every table FK-adjacent to a table already in the statement (either side of
    // the relationship — the new table can be the "many" or the "one" side),
    // excluding tables the statement already references. The graph walk itself is
    // pure Core logic (ForeignKeyMatcher, unit-tested there).
    private List<SqlCompletionData> FkNeighborItems(Snapshot snapshot, string statement, Scope scope)
    {
        // The relation being typed at the caret is not one the statement
        // already joins: it would hide the very table the user is spelling.
        var statementTables = ResolvedReferences(snapshot, scope.Relations(statement, int.MaxValue, exceptAt: scope.WordStart(statement)));
        var items = new List<SqlCompletionData>();
        var index = 0;
        foreach (var (neighborSchema, neighborTable) in ForeignKeyMatcher.FindJoinCandidates(statementTables, snapshot.ForeignKeys))
        {
            var resolvesBare = snapshot.TablesByKey.TryGetValue((neighborSchema, neighborTable), out var neighbor)
                && Resolve(snapshot, "", neighborTable) == neighbor;
            // One the search_path finds over a same-named one it doesn't; in
            // discovery order, each table's joins right under its own row
            // (with nothing typed the list would otherwise show every table
            // before the first join).
            var item = TableItem(neighborSchema, neighborTable, qualified: !resolvesBare,
                (resolvesBare ? FkTablePriority + 1 : FkTablePriority) - (0.01 * index++));
            items.Add(new SqlCompletionData(item.Text, item.Kind, item.InsertText, item.Priority)
            {
                AliasTable = item.AliasTable,
                Detail = item.Detail,
                DescriptionText = "table · FK match",
            });
            items.AddRange(JoinSnippets(snapshot, statement, scope, statementTables, neighborSchema, neighborTable, item.InsertText, item.Priority));
        }

        return items;
    }

    // "customers c ON c.id = o.customer_id" after JOIN, in one accept (§6.4):
    // one row per foreign key that ties the table to the statement's own, its
    // alias the one the auto-alias would pick, right under the table's row.
    private static List<SqlCompletionData> JoinSnippets(
        Snapshot snapshot, string statement, Scope scope, IReadOnlyList<TableReference> statementTables,
        string schema, string table, string tableInsert, double priority)
    {
        var taken = statementTables.SelectMany(t => new[] { t.Table, t.Alias }).OfType<string>().Concat(scope.CteNames(statement));
        var alias = TableAliaser.Derive(table, taken);
        var conditions = ForeignKeyMatcher.BuildJoinConditions([.. statementTables, new TableReference(schema, table, alias)], snapshot.ForeignKeys);
        var items = new List<SqlCompletionData>();
        for (var i = 0; i < conditions.Count; i++)
        {
            var (condition, constraint) = conditions[i];
            items.Add(new SqlCompletionData($"{table} {alias} ON {condition}", SqlCompletionKind.Snippet, $"{tableInsert} {alias} ON {condition}", priority - (0.001 * (i + 1)))
            {
                Detail = constraint ?? schema,
                DescriptionText = constraint is null ? $"join {schema}.{table} on its foreign key" : $"join {schema}.{table} on its foreign key · {constraint}",
            });
        }

        return items;
    }

    // Where the dotted name under the caret starts ("saas.te|" → at "saas").
    private static int QualifiedNameStart(string statement, int caret)
    {
        var start = Math.Clamp(caret, 0, statement.Length);
        while (start > 0 && (SqlLexer.IsIdentPart(statement[start - 1]) || statement[start - 1] is '.' or '"'))
        {
            start--;
        }

        return start;
    }

    // When the caret is in a JOIN's ON condition, the two sides a column of
    // `qualified` would be compared with: the joined relation itself, or (for
    // a column of the joined relation) every relation before it. Null outside
    // an ON, or when a side is not a catalog relation.
    private (CompletionTable Qualified, IReadOnlyList<CompletionTable> Others)? JoinConditionSides(
        Snapshot snapshot, string statement, SqlBlock block, SqlSource qualified, int caret)
    {
        if (!SqlKeywordGrammar.IsInJoinCondition(statement, caret)
            || qualified.Derived is not null || qualified.IsFunction
            || Resolve(snapshot, qualified.Schema, qualified.Name) is not { } table)
        {
            return null;
        }

        var joinedIndex = block.Sources.FindLastIndex(s => s.Start < caret);
        if (joinedIndex < 0)
        {
            return null;
        }

        var joinedSource = block.Sources[joinedIndex];
        var others = new List<CompletionTable>();
        IEnumerable<SqlSource> otherSources = joinedSource == qualified ? block.Sources.Take(joinedIndex) : [joinedSource];
        foreach (var source in otherSources)
        {
            if (source.Derived is null && !source.IsFunction && Resolve(snapshot, source.Schema, source.Name) is { } other)
            {
                others.Add(other);
            }
        }

        return others.Count == 0 ? null : (table, others);
    }

    // A column of `table` that a foreign key ties to one of `others` (either
    // direction) is what the ON most likely compares: it goes first — and an
    // FK the statement doesn't use yet before one it does, which is what the
    // second JOIN of the same table needs (B07: "u.id = i." → assignee_id,
    // reporter_id, not id).
    private static List<SqlCompletionData> RankByForeignKeys(
        Snapshot snapshot, string statement, string qualifier, List<SqlCompletionData> columns,
        CompletionTable table, IReadOnlyList<CompletionTable> others)
    {
        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fk in snapshot.ForeignKeys)
        {
            foreach (var other in others)
            {
                if (fk.FromSchema == table.Schema && fk.FromTable == table.Name && fk.ToSchema == other.Schema && fk.ToTable == other.Name)
                {
                    linked.UnionWith(fk.FromColumns);
                }

                if (fk.ToSchema == table.Schema && fk.ToTable == table.Name && fk.FromSchema == other.Schema && fk.FromTable == other.Name)
                {
                    linked.UnionWith(fk.ToColumns);
                }
            }
        }

        if (linked.Count == 0)
        {
            return columns;
        }

        return [.. columns.Select(c =>
        {
            if (!linked.Contains(c.Text))
            {
                return c;
            }

            var used = statement.Contains($"{qualifier}.{c.InsertText}", StringComparison.OrdinalIgnoreCase);
            return new SqlCompletionData(c.Text, c.Kind, c.InsertText, CurrentColumnPriority + (used ? UsedForeignKeyBoost : ForeignKeyBoost))
            {
                Detail = c.Detail,
                DescriptionText = $"{c.DescriptionText} · foreign key",
            };
        })];
    }

    // The join condition suggestion after ON: pairs the table this ON belongs
    // to — the last one joined *before the caret*, so editing an early ON
    // ignores the JOINs written after it — with the closest earlier table it has
    // a direct FK to, and offers "child.fk_col = parent.pk_col" as the single
    // top item.
    private IReadOnlyList<SqlCompletionData> GetJoinConditionCompletions(Snapshot snapshot, string statement, int caret, Scope scope, IReadOnlyList<string>? operandKeywords)
    {
        var predicateItems = GetPredicateCompletions(snapshot, statement, scope, operandKeywords);
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
    private IReadOnlyList<SqlCompletionData> GetGeneralCompletions(Snapshot snapshot, string statement, Scope scope, IReadOnlyList<string>? operandKeywords) =>
        WithCatalog(CollectStatementItems(snapshot, statement, scope, out _), operandKeywords, snapshot.BaseItems, snapshot.BaseItemsWithoutKeywords);

    // Predicate/row position (WHERE, ON, HAVING, GROUP/ORDER BY, USING): only the
    // statement's sources' columns can be named here. When the statement has
    // sources but none of them resolves, the catalog's columns still stay out —
    // an unknown table is no reason to offer every column in the database; the
    // full catalog is the fallback only for a statement with no sources at all.
    private IReadOnlyList<SqlCompletionData> GetPredicateCompletions(Snapshot snapshot, string statement, Scope scope, IReadOnlyList<string>? operandKeywords)
    {
        var items = CollectStatementItems(snapshot, statement, scope, out var sourceCount);
        return sourceCount == 0
            ? WithCatalog(items, operandKeywords, snapshot.BaseItems, snapshot.BaseItemsWithoutKeywords)
            : WithCatalog(items, operandKeywords, snapshot.PredicateBaseItems, snapshot.PredicateBaseItemsWithoutKeywords);
    }

    // A column as one source exposes it.
    // A column as one source exposes it; Facts and Reference when it is a
    // catalog relation's own column (flags, comment, the FK it follows).
    private readonly record struct SourceColumn(string Name, string? DataType, string Owner)
    {
        public TableColumn? Facts { get; init; }

        public string? Reference { get; init; }
    }

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
                        DescriptionText = ColumnDescription(column, "also in another source"),
                    });
                }
                else
                {
                    items.Add(ColumnItem(column, CurrentColumnPriority));
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
            ? [.. CatalogColumns(snapshot, table)]
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
        // Where the (possibly empty) name under the caret starts.
        public int WordStart(string statement)
        {
            var start = Math.Clamp(Caret, 0, statement.Length);
            while (start > 0 && SqlLexer.IsIdentPart(statement[start - 1]))
            {
                start--;
            }

            return start;
        }

        public IReadOnlyList<SqlCompletionContext.TableRef> Relations(string statement, int before, int exceptAt = -1)
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
                .Where(s => s.Derived is null && !s.IsFunction && s.Name.Length > 0 && s.Start < before && s.Start != exceptAt)
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
                            DescriptionText = ColumnDescription(column, "outer query"),
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
                        DescriptionText = ColumnDescription(column, "also in another source"),
                    });
                }
                else
                {
                    items.Add(ColumnItem(column, CurrentColumnPriority));
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
            var columns = ColumnsOf(snapshot, block, target, []);
            var items = BareColumnItems(columns, listed);
            // ON CONFLICT … DO UPDATE SET: "col = excluded.col", the upsert's
            // usual assignment, one row per column (§6.4).
            if (block.Kind == SqlBlockKind.Insert && block.ClauseAt(caret) == "set" && block.SeesExcluded(caret))
            {
                foreach (var column in (columns ?? []).Where(c => !listed.Contains(c.Name)))
                {
                    var name = SqlIdentifier.QuoteIfNeeded(column.Name);
                    var text = $"{name} = excluded.{name}";
                    items.Add(new SqlCompletionData(text, SqlCompletionKind.Snippet, text, CurrentColumnPriority - 1)
                    {
                        Detail = "excluded",
                        DescriptionText = "take the proposed row's value",
                    });
                }
            }

            return items;
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
        [.. (columns ?? []).Where(c => !listed.Contains(c.Name)).Select(c => ColumnItem(c, CurrentColumnPriority))];

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
                ? [.. CatalogColumns(snapshot, table)]
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

            foreach (var column in CatalogColumns(snapshot, table))
            {
                if (seen.Add(column.Name))
                {
                    columns.Add(column);
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
    private CompletionTable? Resolve(Snapshot snapshot, string schema, string table) =>
        schema.Length > 0
            ? snapshot.TablesByKey.GetValueOrDefault((schema, table))
            : ResolveShort(snapshot.TablesByKey, snapshot.TablesByName, snapshot.Excluded,
                SessionSearchPathChanged ? null : snapshot.SearchPath, table);

    // The table list for table position under the session's current idea of
    // the search_path.
    private CandidateList TableRefItemsOf(Snapshot snapshot) =>
        SessionSearchPathChanged ? snapshot.TableRefItemsUnknownPath : snapshot.TableRefItems;

    private static CompletionTable? ResolveShort(
        IReadOnlyDictionary<(string, string), CompletionTable> tablesByKey,
        IReadOnlyDictionary<string, List<CompletionTable>> tablesByName,
        IReadOnlySet<string> excluded,
        IReadOnlyList<string>? path,
        string table)
    {
        // pg_catalog is searched first unless the path names it somewhere else.
        if (path?.Contains("pg_catalog") != true && tablesByKey.TryGetValue(("pg_catalog", table), out var system))
        {
            return system;
        }

        if (path is not null)
        {
            foreach (var candidate in path)
            {
                if (excluded.Contains(candidate))
                {
                    return null;
                }

                if (tablesByKey.TryGetValue((candidate, table), out var found))
                {
                    return found;
                }
            }

            return null;
        }

        return tablesByName.TryGetValue(table, out var sameName) && sameName.Count == 1 ? sameName[0] : null;
    }

    private static List<SqlCompletionData> ColumnItems(IEnumerable<SourceColumn> columns) =>
        [.. columns.Select(c => ColumnItem(c, CurrentColumnPriority))];

    // The data type rides in Detail (right-aligned in the row); the tooltip
    // names the owning relation, which the row itself doesn't show, and what
    // the catalog knows about the column (E06).
    private static SqlCompletionData ColumnItem(SourceColumn column, double priority, bool isGuess = false) =>
        new(column.Name, SqlCompletionKind.Column, SqlIdentifier.QuoteIfNeeded(column.Name), priority)
        {
            Detail = column.DataType,
            DescriptionText = ColumnDescription(column, null),
            IsGuess = isGuess,
        };

    // "column · orders · PK · not null · → customers.id", its comment on the next line.
    private static string ColumnDescription(SourceColumn column, string? note)
    {
        var parts = new List<string> { "column", column.Owner };
        if (note is not null)
        {
            parts.Add(note);
        }

        if (column.Facts is { } facts)
        {
            if (facts.IsPrimaryKey)
            {
                parts.Add("PK");
            }

            if (facts.Identity != '\0')
            {
                parts.Add(facts.Identity == 'a' ? "identity always" : "identity");
            }
            else if (facts.IsGenerated)
            {
                parts.Add("generated");
            }
            else if (facts.HasDefault)
            {
                parts.Add("default");
            }

            if (facts.NotNull && !facts.IsPrimaryKey)
            {
                parts.Add("not null");
            }
        }

        if (column.Reference is { } reference)
        {
            parts.Add($"→ {reference}");
        }

        var line = string.Join(" · ", parts);
        return column.Facts?.Comment is { Length: > 0 } comment ? $"{line}\n{comment}" : line;
    }

    // A catalog relation's columns, with their facts and the FK each follows.
    private static IEnumerable<SourceColumn> CatalogColumns(Snapshot snapshot, CompletionTable table) =>
        table.Columns.Select(c => new SourceColumn(c.Column, c.DataType, table.Name)
        {
            Facts = c,
            Reference = snapshot.References.GetValueOrDefault((table.Schema, table.Name, c.Column)),
        });

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
    private static IReadOnlyList<SqlCompletionData> Merge(List<SqlCompletionData> head, CandidateList tail)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SqlCompletionData>(head.Count + tail.Count);
        // The kinds the head holds: a tail row of any other kind can't be a
        // duplicate (the key starts with the kind), so it is never looked up.
        var headKinds = 0;
        foreach (var item in head)
        {
            if (seen.Add(KeyOf(item)))
            {
                result.Add(item);
                headKinds |= 1 << (int)item.Kind;
            }
        }

        // No kind in common (keywords over a list of tables): the whole tail
        // goes in as one array copy. Otherwise the kinds come from the list's
        // own byte array, not from the rows, so a row is dereferenced only when
        // it might be a duplicate — reading every row's Kind was a cache miss
        // per row, milliseconds over a hundred thousand tables.
        if ((headKinds & tail.KindMask) == 0)
        {
            result.AddRange(tail.Items);
            return result;
        }

        var items = tail.Items;
        var kinds = tail.Kinds;
        for (var i = 0; i < items.Length; i++)
        {
            if ((headKinds & (1 << kinds[i])) == 0 || !seen.Contains(KeyOf(items[i])))
            {
                result.Add(items[i]);
            }
        }

        return result;
    }

    // One of the snapshot's catalog-wide lists, with each row's kind kept
    // beside it in a byte array (and all of them in one mask), so Merge can
    // tell what to skip without touching a hundred thousand objects.
    private sealed class CandidateList : IReadOnlyList<SqlCompletionData>
    {
        public CandidateList(IReadOnlyList<SqlCompletionData> items)
        {
            Items = [.. items];
            Kinds = new byte[Items.Length];
            for (var i = 0; i < Items.Length; i++)
            {
                Kinds[i] = (byte)Items[i].Kind;
                KindMask |= 1 << Kinds[i];
            }
        }

        public SqlCompletionData[] Items { get; }

        public byte[] Kinds { get; }

        public int KindMask { get; }

        public int Count => Items.Length;

        public SqlCompletionData this[int index] => Items[index];

        public IEnumerator<SqlCompletionData> GetEnumerator() => ((IEnumerable<SqlCompletionData>)Items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
        CandidateList BaseItems,
        CandidateList BaseItemsWithoutKeywords,
        CandidateList TableRefItems,
        CandidateList TableRefItemsUnknownPath,
        CandidateList PredicateBaseItems,
        CandidateList PredicateBaseItemsWithoutKeywords,
        IReadOnlyDictionary<string, List<CompletionFunction>> HintFunctions,
        IReadOnlyList<SqlCompletionData> TypeItems,
        IReadOnlyDictionary<string, char> CallKinds)
    {
        public IReadOnlyDictionary<string, IReadOnlyList<string>> EnumLabels { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

        public IReadOnlyDictionary<(string, string, string), string> References { get; init; } = new Dictionary<(string, string, string), string>();

        public IReadOnlyList<SequenceName> Sequences { get; init; } = [];

        public CompletionCatalog? Catalog { get; init; }

        // pg_proc.prokind of a called name, for SqlKeywordGrammar; '\0' when
        // no callable of that name is known.
        public char CallKindOf(string name) =>
            CallKinds.TryGetValue(name, out var kind) || FallbackCallKinds.TryGetValue(name, out kind) ? kind : '\0';
    }
}
