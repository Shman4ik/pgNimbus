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
/// The outcome of a live <see cref="ExplainService.ExplainAsync"/>: the parsed tree and
/// the raw <c>FORMAT JSON</c> payload the server returned (kept so the plan can be copied
/// or exported as JSON into external tools like pev2, unchanged).
/// </summary>
public sealed record ExplainRun(ExplainResult Result, string Json);

/// <summary>
/// A plan imported from pasted text (<see cref="ExplainService.Import"/>): the parsed
/// tree, the text the plan pane should display (canonical layout for JSON, the pasted
/// text verbatim for a text import), and the raw JSON when the import was JSON (null for
/// a text import, which has no JSON to copy/export).
/// </summary>
public sealed record ImportedPlan(ExplainResult Result, string DisplayText, string? RawJson);

/// <summary>
/// Runs `EXPLAIN (FORMAT JSON [, ANALYZE])` and parses the resulting plan
/// into a navigable tree, rather than leaving callers to parse Postgres's
/// raw JSON shape themselves.
/// </summary>
public sealed class ExplainService(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async Task<ExplainRun> ExplainAsync(string sql, bool analyze, CancellationToken ct)
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
            var planJson = (string)(await command.ExecuteScalarAsync(ct))!;
            return new ExplainRun(Parse(planJson), planJson);
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
        return new ExplainRun(Parse(json), json);
    }

    /// <summary>
    /// Parses the raw `EXPLAIN (FORMAT JSON)` payload. Split out from the DB round-trip
    /// for testability. Tolerant of the shapes external tools/pastes produce: the
    /// standard <c>[{ "Plan": … }]</c> array, a single <c>{ "Plan": … }</c> object, or a
    /// bare plan node (<c>{ "Node Type": … }</c>, with or without the array wrapper).
    /// </summary>
    public static ExplainResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entry = root.ValueKind == JsonValueKind.Array
            ? (root.GetArrayLength() > 0 ? root[0] : throw new FormatException("The EXPLAIN JSON array is empty."))
            : root;

        // The entry is either the wrapper ({ "Plan": …, "Planning Time": … }) or the
        // plan node itself ({ "Node Type": … }) when a tool exported just the tree.
        JsonElement planElement;
        if (entry.TryGetProperty("Plan", out var plan))
        {
            planElement = plan;
        }
        else if (entry.TryGetProperty("Node Type", out _))
        {
            planElement = entry;
        }
        else
        {
            throw new FormatException("Unrecognized EXPLAIN JSON: no \"Plan\" or \"Node Type\" element found.");
        }

        var planningTime = entry.TryGetProperty("Planning Time", out var pt) ? pt.GetDouble() : (double?)null;
        var executionTime = entry.TryGetProperty("Execution Time", out var et) ? et.GetDouble() : (double?)null;

        return new ExplainResult(ParseNode(planElement), planningTime, executionTime);
    }

    /// <summary>
    /// Imports an externally-produced plan pasted by the user — no DB round-trip. Accepts
    /// either <c>FORMAT JSON</c> (the robust interchange format) or <c>FORMAT TEXT</c>
    /// (best-effort, via <see cref="ExplainPlanTextParser"/>), auto-detecting which from
    /// the first non-blank character. <see cref="ImportedPlan.DisplayText"/> is what the
    /// plan pane should show: the canonical text layout for a JSON import, or the pasted
    /// text verbatim for a text import. Throws <see cref="FormatException"/> with a
    /// human-readable message on anything it can't parse, so the import UI can surface it.
    /// </summary>
    public static ImportedPlan Import(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("Nothing to import — paste an EXPLAIN plan first.");
        }

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            ExplainResult result;
            try
            {
                result = Parse(trimmed);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"That doesn't look like valid EXPLAIN JSON: {ex.Message}", ex);
            }

            return new ImportedPlan(result, ExplainTextFormatter.Format(result), trimmed);
        }

        var cleaned = ExplainPlanTextParser.Clean(raw);
        return new ImportedPlan(ExplainPlanTextParser.Parse(cleaned), cleaned, RawJson: null);
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
