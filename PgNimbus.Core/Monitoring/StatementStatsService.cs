using Npgsql;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Monitoring;

/// <summary>
/// One pg_stat_statements entry of the current database: a normalized
/// statement (literals replaced by <c>$1</c>, <c>$2</c> …) with its counters
/// since the entry was created. <paramref name="Query"/> is a preview, cut
/// server-side so a big history doesn't cross the wire in full on every
/// refresh; <see cref="StatementStatsService.GetQueryTextAsync"/> fetches one
/// statement's whole text.
/// </summary>
/// <param name="StatsSince">When this entry started counting (PostgreSQL 17+), or null when the server doesn't say.</param>
public sealed record StatementStat(
    long UserId,
    string Role,
    long QueryId,
    bool TopLevel,
    string Query,
    long Calls,
    double TotalMs,
    long Rows,
    long SharedBlocksHit,
    long SharedBlocksRead,
    DateTime? StatsSince)
{
    /// <summary>What identifies one entry across two reads: pg_stat_statements' own key, minus the database (always the current one).</summary>
    public (long UserId, long QueryId, bool TopLevel) Key => (UserId, QueryId, TopLevel);

    /// <summary>Mean time per call in ms, or 0 when it never ran.</summary>
    public double MeanMs => Calls == 0 ? 0 : TotalMs / Calls;
}

/// <summary>Everything one read of pg_stat_statements returned for the current database.</summary>
/// <param name="TakenAt">The server's clock when the read ran.</param>
/// <param name="StatsReset">When the whole view was last reset (<c>pg_stat_statements_info</c>, extension 1.9+), or null.</param>
/// <param name="Deallocations">How many entries the view has evicted to stay under <c>pg_stat_statements.max</c> (1.9+), or null.</param>
/// <param name="HiddenStatements">Entries of other roles whose text this role may not read (no <c>pg_read_all_stats</c>).</param>
/// <param name="OwnStatements">pgNimbus's own catalog and monitoring reads (<see cref="InternalSql"/>), left out of <paramref name="Statements"/>.</param>
public sealed record StatementStatsSnapshot(
    DateTime TakenAt,
    DateTime? StatsReset,
    long? Deallocations,
    IReadOnlyList<StatementStat> Statements,
    int HiddenStatements,
    int OwnStatements = 0);

/// <summary>Why pg_stat_statements can't be read here, if it can't.</summary>
public enum StatementStatsProblem
{
    None,

    /// <summary>The extension isn't created in this database.</summary>
    NotInstalled,

    /// <summary>The extension exists, but the server wasn't started with it in <c>shared_preload_libraries</c>.</summary>
    NotLoaded,
}

/// <summary>A read of pg_stat_statements: a snapshot, or the reason there isn't one.</summary>
public sealed record StatementStatsRead(StatementStatsProblem Problem, StatementStatsSnapshot? Snapshot);

/// <summary>
/// Reads pg_stat_statements for the slow-query shortlist (ROADMAP Q2). Read-only
/// like the other monitoring services: it never creates the extension, never
/// resets its counters and never changes server settings. Where the view is
/// missing or unreadable it says which, so the window can explain what's needed.
///
/// The column set depends on the <em>extension</em> version, not the server's
/// (a server upgraded with pg_upgrade keeps the old extension until someone runs
/// <c>ALTER EXTENSION … UPDATE</c>), so the columns are read from the catalog
/// rather than guessed from <c>server_version_num</c>: <c>total_exec_time</c>
/// replaced <c>total_time</c> in 1.8, <c>toplevel</c> and the
/// <c>pg_stat_statements_info</c> view arrived in 1.9, and per-entry
/// <c>stats_since</c> in 1.11.
/// </summary>
public sealed class StatementStatsService(NpgsqlDataSource dataSource)
{
    /// <summary>How much of each statement a read brings back; the grid shows one line of it.</summary>
    public const int PreviewLength = 400;

    // What pg_stat_statements shows in place of a statement this role may not see.
    private const string InsufficientPrivilege = "<insufficient privilege>";

    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async Task<StatementStatsRead> ReadAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        if (await FindExtensionSchemaAsync(connection, ct) is not { } schema)
        {
            return new StatementStatsRead(StatementStatsProblem.NotInstalled, null);
        }

        var view = $"{SqlIdentifier.Quote(schema)}.pg_stat_statements";
        var columns = await ColumnsOfAsync(connection, view, ct);
        var totalTime = columns.Contains("total_exec_time") ? "total_exec_time" : "total_time";
        var topLevel = columns.Contains("toplevel") ? "s.toplevel" : "true";
        var statsSince = columns.Contains("stats_since") ? "s.stats_since" : "NULL::timestamptz";

        var sql = $"""
            SELECT s.userid::int8, COALESCE(r.rolname, s.userid::text), s.queryid, {topLevel},
                   COALESCE(left(s.query, {PreviewLength}), ''), s.calls, s.{totalTime}, s.rows,
                   s.shared_blks_hit, s.shared_blks_read, {statsSince}
            FROM {view} s
            LEFT JOIN pg_catalog.pg_roles r ON r.oid = s.userid
            WHERE s.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())
            """;

        var statements = new List<StatementStat>();
        var hidden = 0;
        var own = 0;
        try
        {
            await using var command = new NpgsqlCommand(InternalSql.Tag(sql), connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // Another role's statement without pg_read_all_stats: its counters
                // are there but its text and queryid aren't, so it can be counted
                // but not listed, opened, or matched across two reads.
                if (reader.IsDBNull(2) || reader.GetString(4) == InsufficientPrivilege)
                {
                    hidden++;
                    continue;
                }

                // The app's own reads: counted, not listed. A user looking for
                // what their workload costs doesn't want the client's catalog
                // queries at the top of the list.
                if (InternalSql.IsTagged(reader.GetString(4)))
                {
                    own++;
                    continue;
                }

                statements.Add(new StatementStat(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetBoolean(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetDouble(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetDateTime(10)));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectNotInPrerequisiteState)
        {
            // "pg_stat_statements must be loaded via shared_preload_libraries"
            return new StatementStatsRead(StatementStatsProblem.NotLoaded, null);
        }

        var info = $"{SqlIdentifier.Quote(schema)}.pg_stat_statements_info";
        var hasInfo = await RelationExistsAsync(connection, info, ct);
        await using var meta = new NpgsqlCommand(
            InternalSql.Tag(hasInfo
                ? $"SELECT pg_catalog.now(), stats_reset, dealloc FROM {info}"
                : "SELECT pg_catalog.now(), NULL::timestamptz, NULL::int8"),
            connection);
        await using var metaReader = await meta.ExecuteReaderAsync(ct);
        await metaReader.ReadAsync(ct);

        return new StatementStatsRead(StatementStatsProblem.None, new StatementStatsSnapshot(
            metaReader.GetDateTime(0),
            metaReader.IsDBNull(1) ? null : metaReader.GetDateTime(1),
            metaReader.IsDBNull(2) ? null : metaReader.GetInt64(2),
            statements,
            hidden,
            own));
    }

    /// <summary>
    /// One statement's whole normalized text, for opening it in the editor;
    /// null when the entry is gone (reset or evicted since the list was read).
    /// </summary>
    public async Task<string?> GetQueryTextAsync(StatementStat statement, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        if (await FindExtensionSchemaAsync(connection, ct) is not { } schema)
        {
            return null;
        }

        await using var command = new NpgsqlCommand(
            InternalSql.Tag($"""
            SELECT s.query FROM {SqlIdentifier.Quote(schema)}.pg_stat_statements s
            WHERE s.queryid = @queryid AND s.userid = @userid::oid
              AND s.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())
            LIMIT 1
            """),
            connection);
        command.Parameters.AddWithValue("queryid", statement.QueryId);
        command.Parameters.AddWithValue("userid", statement.UserId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<string?> FindExtensionSchemaAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            InternalSql.Tag("""
            SELECT n.nspname FROM pg_catalog.pg_extension e
            JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
            WHERE e.extname = 'pg_stat_statements'
            """),
            connection);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<HashSet<string>> ColumnsOfAsync(NpgsqlConnection connection, string relation, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            InternalSql.Tag("""
            SELECT a.attname FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = pg_catalog.to_regclass(@relation) AND a.attnum > 0 AND NOT a.attisdropped
            """),
            connection);
        command.Parameters.AddWithValue("relation", relation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<bool> RelationExistsAsync(NpgsqlConnection connection, string relation, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(InternalSql.Tag("SELECT pg_catalog.to_regclass(@relation) IS NOT NULL"), connection);
        command.Parameters.AddWithValue("relation", relation);
        return await command.ExecuteScalarAsync(ct) is true;
    }
}
