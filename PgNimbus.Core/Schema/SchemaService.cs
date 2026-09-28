using Npgsql;

namespace PgNimbus.Core.Schema;

public sealed record SchemaInfo(string Name);

public enum RelationKind
{
    Table,
    View,
    MaterializedView,
    PartitionedTable,
}

/// <summary>
/// A relation in a schema. <paramref name="TotalBytes"/> is
/// <c>pg_total_relation_size</c> (heap + indexes + TOAST) for stored relations
/// (ordinary tables and materialized views); null for views and partitioned
/// parents, which have no meaningful own-storage size to show in the tree.
/// </summary>
public sealed record TableInfo(string Name, RelationKind Kind, long? TotalBytes = null);

public sealed record RelationInfo(string Schema, string Name, RelationKind Kind);

/// <summary>What <see cref="SchemaService.GetWriteStateAsync"/> found a new session allowed to do.</summary>
public enum SessionWriteState
{
    ReadWrite,

    /// <summary><c>default_transaction_read_only</c> is on: from the profile, or set on the role or database.</summary>
    ReadOnly,

    /// <summary>The server is a standby replica (in recovery) and refuses every write.</summary>
    Standby,
}

public sealed record ColumnDetail(string Name, string DataType, bool NotNull, bool IsPrimaryKey)
{
    /// <summary>
    /// The input affordance the column's values call for (enum dropdown,
    /// checkbox, date picker, …), classified from the column's base type with
    /// domains resolved. <see cref="ColumnValueEditor.Text"/> when nothing
    /// more specific applies.
    /// </summary>
    public ColumnValueEditor Editor { get; init; } = ColumnValueEditor.Text;

    /// <summary>The enum type's labels in declared order when <see cref="Editor"/> is <see cref="ColumnValueEditor.Enum"/>; empty otherwise.</summary>
    public IReadOnlyList<string> EnumLabels { get; init; } = [];

    /// <summary>The base type a domain column resolves to (e.g. "integer" for a domain over integer); null when the declared type isn't a domain.</summary>
    public string? DomainBaseType { get; init; }

    /// <summary>
    /// pg_attribute.attnum — the column's stable positional identity within its
    /// table (gaps where columns were dropped). Matches the attribute number the
    /// wire protocol reports per result column, which is what lets a result set
    /// be checked against the table's real columns without trusting names.
    /// </summary>
    public short AttNum { get; init; }
}

public sealed record TableColumn(string Table, string Column, string DataType)
{
    // What completion shows beside a column (sql-completion-audit-2.md E06);
    // left at their defaults by every other reader of TableColumn.

    /// <summary>pg_attribute.attnotnull.</summary>
    public bool NotNull { get; init; }

    /// <summary>The column has a DEFAULT (pg_attribute.atthasdef, generated columns excluded).</summary>
    public bool HasDefault { get; init; }

    /// <summary>pg_attribute.attidentity: <c>a</c> GENERATED ALWAYS, <c>d</c> BY DEFAULT, <c>'\0'</c> not an identity.</summary>
    public char Identity { get; init; }

    /// <summary>A generated (computed) column: it can't be written.</summary>
    public bool IsGenerated { get; init; }

    /// <summary>Part of the relation's primary key.</summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>The column's comment, if it has one.</summary>
    public string? Comment { get; init; }
}

/// <summary>A function/procedure/aggregate/window function. <paramref name="Kind"/> is pg_proc.prokind: f, p, a, or w.</summary>
public sealed record FunctionInfo(string Name, string Arguments, string ReturnType, char Kind)
{
    /// <summary>
    /// The arguments as <c>pg_get_function_arguments</c> spells them — with
    /// their <c>DEFAULT</c>s, which the identity form in <see cref="Arguments"/>
    /// (what DROP FUNCTION needs) leaves out. Null when not read.
    /// </summary>
    public string? FullArguments { get; init; }

    /// <summary>The function's comment (pg_description), if it has one.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Machinery rather than something a query calls: a type's input/output or
    /// support function, the implementation of a boolean operator (a predicate
    /// is written <c>a = b</c>, never <c>vector_eq(a, b)</c>) or of any operator
    /// the server itself describes as "implementation of …", an operator's
    /// selectivity estimator, an index access method's support function or
    /// handler, an aggregate's state function, or anything taking or returning
    /// a pseudo-type only the server can supply (<c>internal</c>, <c>cstring</c>,
    /// <c>trigger</c> …). Read from the catalog, never guessed from the name —
    /// <c>ltree_in</c> and <c>hnsw_bit_support</c> are internal, while
    /// <c>similarity</c> and <c>l2_distance</c> (which also implements the
    /// <c>&lt;-&gt;</c> operator, a distance, not a predicate) are not.
    /// </summary>
    public bool IsInternal { get; init; }
}

/// <summary>
/// A relation as completion lists it: its pg_class.relkind (<c>r</c> table,
/// <c>v</c> view, <c>m</c> materialized view, <c>p</c> partitioned table,
/// <c>f</c> foreign table) and whether it is a partition of another
/// (<c>relispartition</c>) — reached through its parent, so not offered on its own.
/// </summary>
public sealed record CompletionRelationInfo(string Name, char Kind, bool IsPartition)
{
    /// <summary>The relation's comment, if it has one.</summary>
    public string? Comment { get; init; }

    /// <summary>pg_class.reltuples, the planner's row estimate; null before the relation was ever analyzed.</summary>
    public long? RowEstimate { get; init; }
}

/// <summary>
/// A data type a cast can name: <paramref name="Name"/> is pg_type's name,
/// <paramref name="DisplayName"/> what <c>format_type</c> spells (<c>integer</c>
/// for int4, <c>timestamp with time zone</c> for timestamptz), and
/// <paramref name="Kind"/> pg_type.typtype: <c>b</c>ase, <c>d</c>omain,
/// <c>e</c>num, <c>c</c>omposite, <c>r</c>ange, <c>m</c>ultirange.
/// </summary>
public sealed record DataTypeInfo(string Schema, string Name, string DisplayName, char Kind)
{
    /// <summary>An enum's labels in their declared order; empty for any other kind.</summary>
    public IReadOnlyList<string> EnumLabels { get; init; } = [];
}

/// <summary>A sequence completion can name (in <c>nextval('…')</c>, after DROP SEQUENCE …).</summary>
public sealed record SequenceName(string Schema, string Name);

/// <summary>An index completion can name (DROP INDEX, REINDEX INDEX, CLUSTER … USING), with the relation it indexes.</summary>
public sealed record IndexName(string Schema, string Name, string Table);

/// <summary>
/// A server setting from pg_settings: <paramref name="VarType"/> is
/// bool / integer / real / string / enum, <paramref name="EnumValues"/> the
/// values an enum setting takes.
/// </summary>
public sealed record SettingInfo(string Name, string VarType, string? ShortDescription, IReadOnlyList<string> EnumValues);

/// <summary>An extension from pg_available_extensions; <paramref name="InstalledVersion"/> is null when not installed.</summary>
public sealed record ExtensionInfo(string Name, string? InstalledVersion, string DefaultVersion, string? Description)
{
    public bool IsInstalled => InstalledVersion is not null;
}

public sealed record RoleInfo(string Name, bool CanLogin, bool IsSuperuser, bool CanCreateDb, bool CanCreateRole);

/// <summary>
/// A sequence (pg_sequences). <paramref name="LastValue"/> is null when the
/// sequence has never been advanced (no <c>nextval</c> yet), which pg reports as
/// a null last_value rather than the start value.
/// </summary>
public sealed record SequenceInfo(
    string Name, string DataType, long IncrementBy, long? LastValue,
    long StartValue, long MinValue, long MaxValue, bool Cycle);

/// <summary>
/// A user-defined type: enum (<c>e</c>), composite (<c>c</c>), or domain (<c>d</c>),
/// per pg_type.typtype. Only the fields relevant to the kind are populated —
/// <paramref name="EnumLabels"/> for enums, <paramref name="CompositeFields"/> for
/// composites, <paramref name="DomainBaseType"/>/<paramref name="DomainNotNull"/>
/// for domains — the rest are empty/default.
/// </summary>
public sealed record UserTypeInfo(
    string Name, char TypType, string? DomainBaseType, bool DomainNotNull,
    IReadOnlyList<string> EnumLabels, IReadOnlyList<string> CompositeFields);

/// <summary>An index on a relation. <paramref name="Definition"/> is the full <c>pg_get_indexdef</c> for the tooltip.</summary>
public sealed record IndexInfo(string Name, bool IsUnique, bool IsPrimary, string Definition);

/// <summary>A user (non-internal) trigger. <paramref name="Definition"/> is the full <c>pg_get_triggerdef</c>; <paramref name="Enabled"/> is false only for a fully-disabled trigger.</summary>
public sealed record TriggerInfo(string Name, string Definition, bool Enabled);

/// <summary>
/// A foreign key edge: <paramref name="FromColumns"/> on <paramref name="FromSchema"/>.<paramref name="FromTable"/>
/// (the referencing/"child" side) reference <paramref name="ToColumns"/> on
/// <paramref name="ToSchema"/>.<paramref name="ToTable"/> (the referenced/"parent" side), positionally paired.
/// </summary>
public sealed record ForeignKeyInfo(
    string FromSchema, string FromTable, IReadOnlyList<string> FromColumns,
    string ToSchema, string ToTable, IReadOnlyList<string> ToColumns,
    string? ConstraintName = null);

/// <summary>
/// Reads structure straight from pg_catalog rather than relying on
/// information_schema, so it reflects the real Postgres model (matviews,
/// partitioned tables, actual type names) instead of the SQL-standard
/// lowest common denominator.
/// </summary>
public sealed class SchemaService(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT nspname
            FROM pg_catalog.pg_namespace
            WHERE nspname NOT LIKE 'pg\_%'
              AND nspname <> 'information_schema'
            ORDER BY nspname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<SchemaInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SchemaInfo(reader.GetString(0)));
        }

        return results;
    }

    /// <summary>
    /// A schema's relations as completion needs them, without
    /// <see cref="GetTablesAsync"/>'s per-relation size (a stat of every file
    /// of every relation): the name, the kind, and whether it is a partition.
    /// </summary>
    public async Task<IReadOnlyList<CompletionRelationInfo>> GetCompletionRelationsAsync(string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname, c.relkind::text, c.relispartition,
                   pg_catalog.obj_description(c.oid, 'pg_class'),
                   CASE WHEN c.relkind IN ('r', 'm', 'p') THEN c.reltuples::float8 END
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
            ORDER BY c.relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<CompletionRelationInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CompletionRelationInfo(reader.GetString(0), reader.GetString(1)[0], reader.GetBoolean(2))
            {
                Comment = reader.IsDBNull(3) ? null : reader.GetString(3),
                // -1 (PG 14+) on a table never analyzed: no estimate yet.
                RowEstimate = reader.IsDBNull(4) || reader.GetDouble(4) < 0 ? null : (long)reader.GetDouble(4),
            });
        }

        return results;
    }

    /// <summary>
    /// The schemas an unqualified name is looked up in, in order
    /// (<c>current_schemas(false)</c>: the effective <c>search_path</c> minus the
    /// implicit pg_catalog/pg_temp, and minus entries that don't exist). Read from
    /// a pooled connection, so it's the connection's default — a <c>SET
    /// search_path</c> run in one query isn't visible here, and completion treats
    /// the answer as the default rather than as ground truth.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSearchPathAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag("SELECT pg_catalog.current_schemas(false)"), connection);
        return await command.ExecuteScalarAsync(ct) is string[] path ? path : [];
    }

    /// <summary>
    /// Whether a new session on this connection may write, as the server sees
    /// it. Asked of the server rather than read off the profile because three
    /// different things make a session read-only, and only one of them is ours:
    /// a <see cref="Connections.ConnectionProfile.ReadOnly"/> profile, a role or
    /// database with <c>default_transaction_read_only</c> set on the server, and
    /// a standby replica, which refuses every write whatever the session says.
    /// </summary>
    public async Task<SessionWriteState> GetWriteStateAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            InternalSql.Tag("SELECT pg_catalog.pg_is_in_recovery(), pg_catalog.current_setting('default_transaction_read_only') = 'on'"),
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetBoolean(0) ? SessionWriteState.Standby
            : reader.GetBoolean(1) ? SessionWriteState.ReadOnly
            : SessionWriteState.ReadWrite;
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(string schema, CancellationToken ct)
    {
        // Size only for relations with their own storage (ordinary tables and
        // matviews). Views have none; a partitioned parent's own size is 0 and
        // pg_total_relation_size doesn't sum its partitions, so showing it would
        // mislead — leave both null and render no size in the tree.
        const string sql = """
            SELECT c.relname, c.relkind::text,
                   CASE WHEN c.relkind IN ('r', 'm')
                        THEN pg_catalog.pg_total_relation_size(c.oid) END
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'v', 'm', 'p')
            ORDER BY c.relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<TableInfo>();
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(1) switch
            {
                "r" => RelationKind.Table,
                "v" => RelationKind.View,
                "m" => RelationKind.MaterializedView,
                "p" => RelationKind.PartitionedTable,
                _ => RelationKind.Table,
            };

            results.Add(new TableInfo(
                reader.GetString(0),
                kind,
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return results;
    }

    /// <summary>
    /// Every relation (table/view/matview/partitioned table) across all
    /// non-system schemas in one query, schema-qualified — feeds the command
    /// palette's fuzzy "jump to a table" list without an N+1 per-schema walk.
    /// </summary>
    public async Task<IReadOnlyList<RelationInfo>> GetAllRelationsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname, c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT LIKE 'pg\_%'
              AND n.nspname <> 'information_schema'
              AND c.relkind IN ('r', 'v', 'm', 'p')
            ORDER BY n.nspname, c.relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RelationInfo>();
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(2) switch
            {
                "r" => RelationKind.Table,
                "v" => RelationKind.View,
                "m" => RelationKind.MaterializedView,
                "p" => RelationKind.PartitionedTable,
                _ => RelationKind.Table,
            };

            results.Add(new RelationInfo(reader.GetString(0), reader.GetString(1), kind));
        }

        return results;
    }

    public async Task<IReadOnlyList<ColumnDetail>> GetColumnsAsync(string schema, string table, CancellationToken ct)
    {
        // Besides name/type/nullability/PK, each column carries what its
        // values need for type-aware editing: the base type's pg_type identity
        // (domains walked to their base via typbasetype, so a domain over an
        // enum still classifies as an enum) and, for enums, the pg_enum labels.
        const string sql = """
            WITH RECURSIVE cols AS (
                SELECT
                    a.attnum,
                    a.attname,
                    format_type(a.atttypid, a.atttypmod) AS data_type,
                    a.attnotnull,
                    COALESCE(pk.is_primary_key, false) AS is_primary_key,
                    a.atttypid
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN (
                    SELECT con.conrelid, unnest(con.conkey) AS attnum, true AS is_primary_key
                    FROM pg_catalog.pg_constraint con
                    WHERE con.contype = 'p'
                ) pk ON pk.conrelid = a.attrelid AND pk.attnum = a.attnum
                WHERE n.nspname = @schema
                  AND c.relname = @table
                  AND a.attnum > 0
                  AND NOT a.attisdropped
            ),
            walk AS (
                SELECT c.attnum, c.atttypid AS type_oid, 0 AS depth
                FROM cols c
                UNION ALL
                SELECT w.attnum, t.typbasetype, w.depth + 1
                FROM walk w
                JOIN pg_catalog.pg_type t ON t.oid = w.type_oid
                WHERE t.typbasetype <> 0
            ),
            base AS (
                SELECT DISTINCT ON (attnum) attnum, type_oid
                FROM walk
                ORDER BY attnum, depth DESC
            )
            SELECT
                c.attname,
                c.data_type,
                c.attnotnull,
                c.is_primary_key,
                bt.typname,
                bt.typtype::text,
                bt.typcategory::text,
                CASE WHEN c.atttypid <> bt.oid THEN format_type(bt.oid, NULL) END AS domain_base_type,
                CASE WHEN bt.typtype = 'e' THEN
                    (SELECT array_agg(e.enumlabel ORDER BY e.enumsortorder)
                     FROM pg_catalog.pg_enum e
                     WHERE e.enumtypid = bt.oid)
                END AS enum_labels,
                c.attnum
            FROM cols c
            JOIN base b ON b.attnum = c.attnum
            JOIN pg_catalog.pg_type bt ON bt.oid = b.type_oid
            ORDER BY c.attnum
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<ColumnDetail>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ColumnDetail(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3))
            {
                Editor = ColumnValueEditorClassifier.Classify(
                    reader.GetString(5)[0],
                    reader.GetString(6)[0],
                    reader.GetString(4)),
                DomainBaseType = reader.IsDBNull(7) ? null : reader.GetString(7),
                EnumLabels = reader.IsDBNull(8) ? [] : reader.GetFieldValue<string[]>(8),
                AttNum = reader.GetInt16(9),
            });
        }

        return results;
    }

    /// <summary>
    /// Resolves a pg_class OID (as the wire protocol reports per result column)
    /// to its schema-qualified name — but only for relations whose rows can be
    /// UPDATEd by primary key directly: ordinary and partitioned tables. Views,
    /// materialized views, and anything else return null, so a result set that
    /// reads through them never gets offered inline editing.
    /// </summary>
    public async Task<RelationInfo?> GetEditableTableByOidAsync(uint tableOid, CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname, c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.oid = @oid
              AND c.relkind IN ('r', 'p')
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        // uint has no implicit Npgsql parameter mapping — the oid type must be named.
        command.Parameters.Add(new NpgsqlParameter<uint>("oid", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = tableOid });
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var kind = reader.GetString(2) == "p" ? RelationKind.PartitionedTable : RelationKind.Table;
        return new RelationInfo(reader.GetString(0), reader.GetString(1), kind);
    }

    /// <summary>Functions, procedures, aggregates, and window functions in a schema, with identity arguments and result type.</summary>
    public async Task<IReadOnlyList<FunctionInfo>> GetFunctionsAsync(string schema, CancellationToken ct)
    {
        // `internal`: see FunctionInfo.IsInternal. Every test is a catalog
        // fact, one EXISTS per place the server keeps machinery.
        const string sql = """
            SELECT p.proname,
                   pg_catalog.pg_get_function_identity_arguments(p.oid),
                   COALESCE(pg_catalog.pg_get_function_result(p.oid), ''),
                   p.prokind::text,
                   pg_catalog.pg_get_function_arguments(p.oid),
                   pg_catalog.obj_description(p.oid, 'pg_proc'),
                   (EXISTS (SELECT 1 FROM pg_catalog.pg_type t
                            WHERE p.oid IN (t.typinput, t.typoutput, t.typreceive, t.typsend,
                                            t.typmodin, t.typmodout, t.typanalyze, t.typsubscript))
                    OR EXISTS (SELECT 1 FROM pg_catalog.pg_operator o
                               WHERE p.oid IN (o.oprrest, o.oprjoin)
                                  OR (o.oprcode = p.oid AND o.oprresult = 'pg_catalog.bool'::pg_catalog.regtype))
                    OR COALESCE(pg_catalog.obj_description(p.oid, 'pg_proc') LIKE 'implementation of %', false)
                    OR EXISTS (SELECT 1 FROM pg_catalog.pg_amproc a WHERE a.amproc = p.oid)
                    OR EXISTS (SELECT 1 FROM pg_catalog.pg_aggregate g
                               WHERE p.oid IN (g.aggtransfn, g.aggfinalfn, g.aggcombinefn, g.aggserialfn,
                                               g.aggdeserialfn, g.aggmtransfn, g.aggminvtransfn, g.aggmfinalfn))
                    OR EXISTS (SELECT 1 FROM pg_catalog.pg_am m WHERE m.amhandler = p.oid)
                    OR EXISTS (SELECT 1 FROM pg_catalog.pg_type rt
                               WHERE rt.oid = p.prorettype
                                 AND rt.typname IN ('internal', 'cstring', 'trigger', 'event_trigger', 'language_handler',
                                                    'fdw_handler', 'index_am_handler', 'tsm_handler', 'table_am_handler'))
                    OR EXISTS (SELECT 1 FROM pg_catalog.unnest(p.proargtypes) a(oid)
                               JOIN pg_catalog.pg_type at ON at.oid = a.oid
                               WHERE at.typname IN ('internal', 'cstring'))) AS is_internal
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema
            ORDER BY p.proname, 2
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<FunctionInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new FunctionInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)[0])
            {
                FullArguments = reader.IsDBNull(4) ? null : reader.GetString(4),
                Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsInternal = reader.GetBoolean(6),
            });
        }

        return results;
    }

    /// <summary>
    /// Every type a cast can name (<c>value::type</c>, <c>CAST(… AS type)</c>):
    /// base, enum, range and multirange types, domains, and free-standing
    /// composites — not table row types, array types (<c>type[]</c> is written
    /// on top of the element) or pseudo/internal types. Read once per completion
    /// refresh, never per keystroke.
    /// </summary>
    public async Task<IReadOnlyList<DataTypeInfo>> GetTypesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, t.typname, pg_catalog.format_type(t.oid, NULL), t.typtype::text,
                   (SELECT pg_catalog.array_agg(e.enumlabel::text ORDER BY e.enumsortorder)
                    FROM pg_catalog.pg_enum e WHERE e.enumtypid = t.oid)
            FROM pg_catalog.pg_type t
            JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            LEFT JOIN pg_catalog.pg_class c ON c.oid = t.typrelid
            WHERE t.typtype IN ('b', 'd', 'e', 'c', 'r', 'm')
              AND (t.typrelid = 0 OR c.relkind = 'c')
              AND t.typcategory NOT IN ('A', 'P', 'X', 'Z')
              AND n.nspname <> 'information_schema'
              AND n.nspname NOT LIKE 'pg\_toast%' AND n.nspname NOT LIKE 'pg\_temp%'
              AND NOT (n.nspname = 'pg_catalog' AND t.typname LIKE 'pg\_%')
            ORDER BY n.nspname, 3
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<DataTypeInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DataTypeInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)[0])
            {
                EnumLabels = reader.IsDBNull(4) ? [] : reader.GetFieldValue<string[]>(4),
            });
        }

        return results;
    }

    /// <summary>All extensions the server knows, installed first — installed ones carry their version, the rest are available to install.</summary>
    public async Task<IReadOnlyList<ExtensionInfo>> GetExtensionsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT a.name, e.extversion, a.default_version, a.comment
            FROM pg_catalog.pg_available_extensions a
            LEFT JOIN pg_catalog.pg_extension e ON e.extname = a.name
            ORDER BY (e.extversion IS NULL), a.name
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<ExtensionInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ExtensionInfo(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return results;
    }

    /// <summary>Non-system roles with the attribute flags worth showing at a glance.</summary>
    public async Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT rolname, rolcanlogin, rolsuper, rolcreatedb, rolcreaterole
            FROM pg_catalog.pg_roles
            WHERE rolname NOT LIKE 'pg\_%'
            ORDER BY rolname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RoleInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RoleInfo(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return results;
    }

    /// <summary>
    /// Every case-preserved identifier a query might name — schema, relation, and
    /// column names across all user schemas — collected in a single round trip.
    /// Feeds <see cref="Query.IdentifierReconciler"/>, which only needs the set of
    /// real spellings (not their namespaces) to reconcile an unquoted query.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCatalogNamesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT nspname AS name
            FROM pg_catalog.pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
            UNION
            SELECT c.relname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
              AND c.relkind IN ('r', 'v', 'm', 'p')
            UNION
            SELECT a.attname
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
              AND c.relkind IN ('r', 'v', 'm', 'p')
              AND a.attnum > 0 AND NOT a.attisdropped
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// Column names (with their formatted data types) for every table/view in a
    /// schema, in one query - used to power SQL autocomplete without an N+1
    /// GetColumnsAsync call per table. Each carries the facts completion shows
    /// beside it: NOT NULL, a default, identity, generated, primary key, comment.
    /// </summary>
    public async Task<IReadOnlyList<TableColumn>> GetAllColumnsAsync(string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod),
                   a.attnotnull, a.atthasdef AND a.attgenerated = '', a.attidentity::text, a.attgenerated <> '',
                   EXISTS (SELECT 1 FROM pg_catalog.pg_constraint k
                           WHERE k.conrelid = c.oid AND k.contype = 'p' AND a.attnum = ANY (k.conkey)),
                   pg_catalog.col_description(c.oid, a.attnum)
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'v', 'm', 'p', 'f')
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY c.relname, a.attnum
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<TableColumn>();
        while (await reader.ReadAsync(ct))
        {
            var identity = reader.GetString(5);
            results.Add(new TableColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            {
                NotNull = reader.GetBoolean(3),
                HasDefault = reader.GetBoolean(4),
                Identity = identity.Length == 0 ? '\0' : identity[0],
                IsGenerated = reader.GetBoolean(6),
                IsPrimaryKey = reader.GetBoolean(7),
                Comment = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return results;
    }

    /// <summary>Every sequence outside the system schemas, by schema and name — for completion, which needs nothing else.</summary>
    public async Task<IReadOnlyList<SequenceName>> GetSequenceNamesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S'
              AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
            ORDER BY 1, 2
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<SequenceName>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SequenceName(reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }

    /// <summary>Every index outside the system schemas, with its table — for completion.</summary>
    public async Task<IReadOnlyList<IndexName>> GetIndexNamesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname, t.relname
            FROM pg_catalog.pg_index x
            JOIN pg_catalog.pg_class c ON c.oid = x.indexrelid
            JOIN pg_catalog.pg_class t ON t.oid = x.indrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
            ORDER BY 1, 2
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<IndexName>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new IndexName(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return results;
    }

    /// <summary>The server's settings (pg_settings), for completion after SET / SHOW.</summary>
    public async Task<IReadOnlyList<SettingInfo>> GetSettingsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT name, vartype, short_desc, enumvals
            FROM pg_catalog.pg_settings
            ORDER BY name
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<SettingInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SettingInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? [] : reader.GetFieldValue<string[]>(3)));
        }

        return results;
    }

    /// <summary>
    /// Every foreign key across all non-system schemas in one query — the edges
    /// that power completion's "rank JOIN targets by FK" and "offer the join
    /// condition" magic. <c>unnest(conkey, confkey) WITH ORDINALITY</c> zips the
    /// referencing/referenced column-number arrays positionally so a composite
    /// key's columns pair up correctly after the <c>array_agg</c>.
    /// </summary>
    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT ns.nspname, c.relname, array_agg(a.attname ORDER BY k.ord),
                   fns.nspname, fc.relname, array_agg(fa.attname ORDER BY k.ord), con.conname
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid = c.relnamespace
            JOIN pg_catalog.pg_class fc ON fc.oid = con.confrelid
            JOIN pg_catalog.pg_namespace fns ON fns.oid = fc.relnamespace
            JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(conkey, confkey, ord) ON true
            JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.conkey
            JOIN pg_catalog.pg_attribute fa ON fa.attrelid = con.confrelid AND fa.attnum = k.confkey
            WHERE con.contype = 'f'
              AND ns.nspname NOT LIKE 'pg\_%' AND ns.nspname <> 'information_schema'
            GROUP BY con.oid, con.conname, ns.nspname, c.relname, fns.nspname, fc.relname
            ORDER BY ns.nspname, c.relname, con.conname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<ForeignKeyInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ForeignKeyInfo(
                reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<string[]>(5), reader.GetString(6)));
        }

        return results;
    }

    /// <summary>Sequences in a schema (pg_sequences), with the headline numbers worth a tooltip.</summary>
    public async Task<IReadOnlyList<SequenceInfo>> GetSequencesAsync(string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT s.sequencename, s.data_type::text, s.increment_by, s.last_value,
                   s.start_value, s.min_value, s.max_value, s.cycle
            FROM pg_catalog.pg_sequences s
            WHERE s.schemaname = @schema
            ORDER BY s.sequencename
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<SequenceInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SequenceInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetBoolean(7)));
        }

        return results;
    }

    /// <summary>
    /// User-defined types in a schema: enums, standalone composites, and domains.
    /// Table row-types (the composite pg auto-creates per table) and array types
    /// are excluded — only types a user actually declares. Each row carries just
    /// the detail its kind needs (enum labels / composite fields / domain base).
    /// </summary>
    public async Task<IReadOnlyList<UserTypeInfo>> GetUserTypesAsync(string schema, CancellationToken ct)
    {
        const string sql = """
            SELECT t.typname,
                   t.typtype::text,
                   CASE WHEN t.typtype = 'd' THEN pg_catalog.format_type(t.typbasetype, t.typtypmod) END,
                   CASE WHEN t.typtype = 'd' THEN t.typnotnull ELSE false END,
                   CASE WHEN t.typtype = 'e' THEN
                       (SELECT array_agg(e.enumlabel ORDER BY e.enumsortorder)
                        FROM pg_catalog.pg_enum e WHERE e.enumtypid = t.oid)
                   END,
                   CASE WHEN t.typtype = 'c' THEN
                       (SELECT array_agg(a.attname || ' ' || pg_catalog.format_type(a.atttypid, a.atttypmod) ORDER BY a.attnum)
                        FROM pg_catalog.pg_attribute a
                        WHERE a.attrelid = t.typrelid AND a.attnum > 0 AND NOT a.attisdropped)
                   END
            FROM pg_catalog.pg_type t
            JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname = @schema
              AND t.typtype IN ('e', 'c', 'd')
              AND (t.typrelid = 0
                   OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid AND c.relkind = 'c'))
            ORDER BY t.typname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<UserTypeInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new UserTypeInfo(
                reader.GetString(0),
                reader.GetString(1)[0],
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? [] : reader.GetFieldValue<string[]>(4),
                reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5)));
        }

        return results;
    }

    /// <summary>Indexes on a relation, primary key first, each with its full definition for a tooltip.</summary>
    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT ic.relname, i.indisunique, i.indisprimary, pg_catalog.pg_get_indexdef(i.indexrelid)
            FROM pg_catalog.pg_index i
            JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_catalog.pg_class tc ON tc.oid = i.indrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = tc.relnamespace
            WHERE n.nspname = @schema AND tc.relname = @table
            ORDER BY i.indisprimary DESC, ic.relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<IndexInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new IndexInfo(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetString(3)));
        }

        return results;
    }

    /// <summary>User (non-internal) triggers on a relation. Constraint/FK-enforcement triggers are excluded via tgisinternal.</summary>
    public async Task<IReadOnlyList<TriggerInfo>> GetTriggersAsync(string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT t.tgname, pg_catalog.pg_get_triggerdef(t.oid, true), (t.tgenabled <> 'D')
            FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table AND NOT t.tgisinternal
            ORDER BY t.tgname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<TriggerInfo>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TriggerInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2)));
        }

        return results;
    }
}
