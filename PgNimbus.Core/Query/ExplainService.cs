using System.Text.Json;
using Npgsql;

namespace PgNimbus.Core.Query;

/// <summary>A single node in a Postgres query plan tree, as parsed from `EXPLAIN (FORMAT JSON)`.</summary>
public sealed record ExplainNode(
    string NodeType,
    string? RelationName,
    string? Alias,
    string? IndexName,
    string? JoinType,
    string? ScanDirection,
    string? SubplanName,
    string? Strategy,
    string? PartialMode,
    string? Operation,
    bool ParallelAware,
    double StartupCost,
    double TotalCost,
    long PlanRows,
    int PlanWidth,
    double? ActualStartupTimeMs,
    double? ActualTotalTimeMs,
    double? ActualRows,
    long? ActualLoops,
    IReadOnlyList<KeyValuePair<string, string>> Details,
    IReadOnlyList<ExplainNode> Children);

public sealed record ExplainResult(ExplainNode Root, double? PlanningTimeMs, double? ExecutionTimeMs);

/// <summary>
/// Runs `EXPLAIN (FORMAT JSON [, ANALYZE])` and parses the resulting plan
/// into a navigable tree, rather than leaving callers to parse Postgres's
/// raw JSON shape themselves.
/// </summary>
public sealed class ExplainService
{
    private readonly NpgsqlDataSource _dataSource;

    public ExplainService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ExplainResult> ExplainAsync(string sql, bool analyze, CancellationToken ct)
    {
        // Plain EXPLAIN omits "Planning Time" unless SUMMARY is requested explicitly
        // (ANALYZE defaults SUMMARY to true already, so it's fine either way there).
        // BUFFERS (I/O counters) is the most-requested EXPLAIN option and is what the
        // disk-spill / lossy-bitmap analysis reads; SETTINGS surfaces the non-default
        // planner GUCs that shaped the plan. Both are cheap to always ask for.
        var options = analyze
            ? "ANALYZE, FORMAT JSON, BUFFERS true, TIMING true, SETTINGS"
            : "FORMAT JSON, SUMMARY, SETTINGS";
        var explainSql = $"EXPLAIN ({options}) {sql}";

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Plain EXPLAIN only plans — it never executes the statement, so no guard is needed.
        if (!analyze)
        {
            await using var command = new NpgsqlCommand(explainSql, connection);
            return Parse((string)(await command.ExecuteScalarAsync(ct))!);
        }

        // EXPLAIN ANALYZE *runs* the statement. Wrap it in a transaction we always
        // roll back, so an ANALYZE of an INSERT/UPDATE/DELETE/MERGE (or a
        // data-modifying CTE) never persists its changes — harmless for reads, since
        // a read-only statement has nothing to commit either way. (Non-transactional
        // side effects like nextval() still can't be undone; that's inherent to
        // EXPLAIN ANALYZE.)
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var analyzeCommand = new NpgsqlCommand(explainSql, connection, transaction);
        var json = (string)(await analyzeCommand.ExecuteScalarAsync(ct))!;
        await transaction.RollbackAsync(ct);
        return Parse(json);
    }

    /// <summary>Parses the raw `EXPLAIN (FORMAT JSON)` payload. Split out from the DB round-trip for testability.</summary>
    public static ExplainResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var planEntry = document.RootElement[0];
        var planningTime = planEntry.TryGetProperty("Planning Time", out var pt) ? pt.GetDouble() : (double?)null;
        var executionTime = planEntry.TryGetProperty("Execution Time", out var et) ? et.GetDouble() : (double?)null;

        return new ExplainResult(ParseNode(planEntry.GetProperty("Plan")), planningTime, executionTime);
    }

    /// <summary>
    /// Node properties consumed into first-class <see cref="ExplainNode"/> fields (or deliberately
    /// dropped as noise) — everything else lands in <see cref="ExplainNode.Details"/> so the text
    /// view can show Filter / Sort Key / Hash Cond / … lines the way `EXPLAIN (FORMAT TEXT)` does.
    /// </summary>
    private static readonly HashSet<string> StructuralKeys =
    [
        "Node Type", "Plans",
        "Relation Name", "Function Name", "CTE Name", "Alias", "Index Name",
        "Join Type", "Scan Direction", "Subplan Name", "Strategy", "Partial Mode", "Operation",
        "Parallel Aware", "Async Capable", "Parent Relationship",
        "Startup Cost", "Total Cost", "Plan Rows", "Plan Width",
        "Actual Startup Time", "Actual Total Time", "Actual Rows", "Actual Loops",
    ];

    private static ExplainNode ParseNode(JsonElement element)
    {
        var children = new List<ExplainNode>();
        if (element.TryGetProperty("Plans", out var childPlans))
        {
            foreach (var child in childPlans.EnumerateArray())
            {
                children.Add(ParseNode(child));
            }
        }

        var details = new List<KeyValuePair<string, string>>();
        foreach (var property in element.EnumerateObject())
        {
            if (StructuralKeys.Contains(property.Name))
            {
                continue;
            }

            // PG 18 stamps "Disabled": false on every node — only a true value is worth a line.
            if (property.Name == "Disabled" && property.Value.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            // BUFFERS emits every counter in FORMAT JSON even when zero (unlike FORMAT TEXT,
            // which hides them). Drop the zero-valued buffer lines so the text view stays clean.
            if (property.Name.EndsWith("Blocks", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.GetDouble() == 0)
            {
                continue;
            }

            if (RenderDetailValue(property.Value) is { } rendered)
            {
                details.Add(new KeyValuePair<string, string>(property.Name, rendered));
            }
        }

        return new ExplainNode(
            element.GetProperty("Node Type").GetString()!,
            GetString(element, "Relation Name") ?? GetString(element, "Function Name") ?? GetString(element, "CTE Name"),
            GetString(element, "Alias"),
            GetString(element, "Index Name"),
            GetString(element, "Join Type"),
            GetString(element, "Scan Direction"),
            GetString(element, "Subplan Name"),
            GetString(element, "Strategy"),
            GetString(element, "Partial Mode"),
            GetString(element, "Operation"),
            element.TryGetProperty("Parallel Aware", out var pa) && pa.ValueKind == JsonValueKind.True,
            element.GetProperty("Startup Cost").GetDouble(),
            element.GetProperty("Total Cost").GetDouble(),
            // Row counts read as double then truncated / kept fractional deliberately:
            // PostgreSQL 18 reports actual rows averaged over loops with two decimals
            // ("Actual Rows": 7.00) — GetInt64() throws FormatException on those.
            (long)element.GetProperty("Plan Rows").GetDouble(),
            element.GetProperty("Plan Width").GetInt32(),
            element.TryGetProperty("Actual Startup Time", out var ast) ? ast.GetDouble() : null,
            element.TryGetProperty("Actual Total Time", out var att) ? att.GetDouble() : null,
            element.TryGetProperty("Actual Rows", out var ar) ? ar.GetDouble() : null,
            element.TryGetProperty("Actual Loops", out var al) ? (long)al.GetDouble() : null,
            details,
            children);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Scalar-ish JSON values render as text; objects (and arrays of them) are skipped as noise.</summary>
    private static string? RenderDetailValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array when value.GetArrayLength() > 0 && value.EnumerateArray().All(IsScalar) =>
            string.Join(", ", value.EnumerateArray().Select(item => RenderDetailValue(item)!)),
        _ => null,
    };

    private static bool IsScalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
}
