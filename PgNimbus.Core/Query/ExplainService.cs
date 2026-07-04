using System.Text.Json;
using Npgsql;

namespace PgNimbus.Core.Query;

/// <summary>A single node in a Postgres query plan tree, as parsed from `EXPLAIN (FORMAT JSON)`.</summary>
public sealed record ExplainNode(
    string NodeType,
    string? RelationName,
    string? IndexName,
    double StartupCost,
    double TotalCost,
    long PlanRows,
    int PlanWidth,
    double? ActualStartupTimeMs,
    double? ActualTotalTimeMs,
    long? ActualRows,
    long? ActualLoops,
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
        var options = analyze ? "ANALYZE, FORMAT JSON, BUFFERS false, TIMING true" : "FORMAT JSON";
        var explainSql = $"EXPLAIN ({options}) {sql}";

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(explainSql, connection);
        var json = (string)(await command.ExecuteScalarAsync(ct))!;

        using var document = JsonDocument.Parse(json);
        var planEntry = document.RootElement[0];
        var planningTime = planEntry.TryGetProperty("Planning Time", out var pt) ? pt.GetDouble() : (double?)null;
        var executionTime = planEntry.TryGetProperty("Execution Time", out var et) ? et.GetDouble() : (double?)null;

        return new ExplainResult(ParseNode(planEntry.GetProperty("Plan")), planningTime, executionTime);
    }

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

        return new ExplainNode(
            element.GetProperty("Node Type").GetString()!,
            element.TryGetProperty("Relation Name", out var rel) ? rel.GetString() : null,
            element.TryGetProperty("Index Name", out var idx) ? idx.GetString() : null,
            element.GetProperty("Startup Cost").GetDouble(),
            element.GetProperty("Total Cost").GetDouble(),
            element.GetProperty("Plan Rows").GetInt64(),
            element.GetProperty("Plan Width").GetInt32(),
            element.TryGetProperty("Actual Startup Time", out var ast) ? ast.GetDouble() : null,
            element.TryGetProperty("Actual Total Time", out var att) ? att.GetDouble() : null,
            element.TryGetProperty("Actual Rows", out var ar) ? ar.GetInt64() : null,
            element.TryGetProperty("Actual Loops", out var al) ? al.GetInt64() : null,
            children);
    }
}
