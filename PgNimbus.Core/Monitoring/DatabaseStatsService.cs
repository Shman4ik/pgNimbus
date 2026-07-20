using Npgsql;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Monitoring;

/// <summary>
/// Database-wide health at a glance: on-disk size and the buffer-cache hit
/// ratios (what fraction of block reads were served from shared_buffers rather
/// than the OS/disk). Ratios are 0..1, or null when the counters are all zero
/// (a freshly-started server that hasn't touched a heap/index yet).
/// </summary>
public sealed record DatabaseOverview(
    string DatabaseName,
    long SizeBytes,
    double? TableCacheHitRatio,
    double? IndexCacheHitRatio);

/// <summary>
/// One relation with its size broken into heap-plus-TOAST vs. index storage,
/// and the planner's live-row estimate. <paramref name="TotalBytes"/> is
/// <c>pg_total_relation_size</c>; <see cref="TableBytes"/> + <see cref="IndexBytes"/>
/// are the <c>pg_table_size</c>/<c>pg_indexes_size</c> split.
/// </summary>
public sealed record RelationSize(
    string Schema,
    string Name,
    RelationKind Kind,
    long TotalBytes,
    long TableBytes,
    long IndexBytes,
    long RowEstimate);

/// <summary>
/// A table's scan counters from pg_stat_user_tables: sequential vs. index
/// scans and the dead-tuple count. <see cref="IndexScanRatio"/> answers "how
/// often does this table get read via an index?" — a low ratio on a big table
/// is the classic missing-index smell.
/// </summary>
public sealed record TableScanUsage(
    string Schema,
    string Name,
    long SeqScan,
    long SeqTupRead,
    long IdxScan,
    long IdxTupFetch,
    long LiveTuples,
    long DeadTuples)
{
    /// <summary>Fraction of scans that used an index (0..1), or null when the table has never been scanned either way.</summary>
    public double? IndexScanRatio =>
        SeqScan + IdxScan == 0 ? null : (double)IdxScan / (SeqScan + IdxScan);
}

/// <summary>
/// An index the planner has never used since stats were last reset
/// (<c>idx_scan = 0</c>), with the disk it's costing. Unique/primary-key
/// indexes are excluded upstream — they earn their keep enforcing constraints
/// regardless of scan count, so flagging them as "unused" would be wrong.
/// </summary>
public sealed record UnusedIndex(
    string Schema,
    string Table,
    string Index,
    long IndexBytes);

/// <summary>
/// Read-only, catalog-driven database statistics behind the Database Overview
/// panel. Every query hits pg_catalog / the pg_stat_* views directly — nothing
/// here writes, so it's always safe to run against production.
/// </summary>
public sealed class DatabaseStatsService
{
    private readonly NpgsqlDataSource _dataSource;

    public DatabaseStatsService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>Current database name, size on disk, and the heap/index cache-hit ratios.</summary>
    public async Task<DatabaseOverview> GetOverviewAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT current_database(),
                   pg_catalog.pg_database_size(current_database()),
                   (SELECT sum(heap_blks_hit)::float8
                           / NULLIF(sum(heap_blks_hit) + sum(heap_blks_read), 0)
                    FROM pg_catalog.pg_statio_user_tables),
                   (SELECT sum(idx_blks_hit)::float8
                           / NULLIF(sum(idx_blks_hit) + sum(idx_blks_read), 0)
                    FROM pg_catalog.pg_statio_user_indexes)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new DatabaseOverview(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3));
    }

    /// <summary>The <paramref name="limit"/> largest tables/matviews by total size, biggest first.</summary>
    public async Task<IReadOnlyList<RelationSize>> GetLargestRelationsAsync(int limit, CancellationToken ct)
    {
        // Ordinary tables and matviews only — a partitioned parent's own
        // pg_total_relation_size is ~0 (it doesn't sum its partitions), so
        // listing it would mislead. Its data-holding partitions are ordinary
        // 'r' tables and show up individually. Mirrors SchemaService.GetTablesAsync.
        const string sql = """
            SELECT n.nspname, c.relname, c.relkind::text,
                   pg_catalog.pg_total_relation_size(c.oid),
                   pg_catalog.pg_table_size(c.oid),
                   pg_catalog.pg_indexes_size(c.oid),
                   COALESCE(s.n_live_tup, 0)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_stat_user_tables s ON s.relid = c.oid
            WHERE c.relkind IN ('r', 'm')
              AND n.nspname NOT LIKE 'pg\_%'
              AND n.nspname <> 'information_schema'
            ORDER BY pg_catalog.pg_total_relation_size(c.oid) DESC, n.nspname, c.relname
            LIMIT @limit
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<RelationSize>();
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(2) == "m" ? RelationKind.MaterializedView : RelationKind.Table;

            results.Add(new RelationSize(
                reader.GetString(0),
                reader.GetString(1),
                kind,
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return results;
    }

    /// <summary>Per-table scan counters, the tables scanned most sequentially first (the missing-index suspects).</summary>
    public async Task<IReadOnlyList<TableScanUsage>> GetTableScanUsageAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT schemaname, relname,
                   COALESCE(seq_scan, 0), COALESCE(seq_tup_read, 0),
                   COALESCE(idx_scan, 0), COALESCE(idx_tup_fetch, 0),
                   COALESCE(n_live_tup, 0), COALESCE(n_dead_tup, 0)
            FROM pg_catalog.pg_stat_user_tables
            ORDER BY COALESCE(seq_scan, 0) DESC, COALESCE(seq_tup_read, 0) DESC, relname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<TableScanUsage>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TableScanUsage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return results;
    }

    /// <summary>
    /// Non-constraint indexes that have never been used since the last stats
    /// reset, largest first — the ones worth dropping. Unique, primary-key, and
    /// exclusion-constraint indexes are excluded: they exist to enforce a
    /// constraint, not to be scanned, so a zero scan count doesn't make them dead.
    /// </summary>
    public async Task<IReadOnlyList<UnusedIndex>> GetUnusedIndexesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT s.schemaname, s.relname, s.indexrelname,
                   pg_catalog.pg_relation_size(s.indexrelid)
            FROM pg_catalog.pg_stat_user_indexes s
            JOIN pg_catalog.pg_index i ON i.indexrelid = s.indexrelid
            WHERE COALESCE(s.idx_scan, 0) = 0
              AND NOT i.indisunique
              AND NOT i.indisprimary
              AND NOT i.indisexclusion
            ORDER BY pg_catalog.pg_relation_size(s.indexrelid) DESC, s.indexrelname
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<UnusedIndex>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new UnusedIndex(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }

        return results;
    }
}
