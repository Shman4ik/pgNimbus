using System.Globalization;
using System.Text;

namespace PgNimbus.Core.Query;

/// <summary>
/// Renders a parsed <see cref="ExplainResult"/> in the classic
/// `EXPLAIN (FORMAT TEXT)` layout — node headers with cost/actual figures,
/// indented detail lines, `-&gt;` arrows for children — without a second server
/// round-trip (an EXPLAIN ANALYZE re-run would execute the query again).
/// </summary>
public static class ExplainTextFormatter
{
    public static string Format(ExplainResult result)
    {
        var sb = new StringBuilder();
        AppendNode(sb, result.Root, textIndent: 0, isRoot: true);
        if (result.PlanningTimeMs is { } planning)
        {
            sb.AppendLine(Invariant($"Planning Time: {planning:F3} ms"));
        }

        if (result.ExecutionTimeMs is { } execution)
        {
            sb.AppendLine(Invariant($"Execution Time: {execution:F3} ms"));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The node's text-format display name ("Parallel Hash Left Join",
    /// "Index Scan using orders_pkey on orders o") — also the tree view's title,
    /// so both plan views name nodes identically.
    /// </summary>
    public static string HeaderFor(ExplainNode node)
    {
        var name = node.NodeType;

        // Joins fold their join type into the name the way explain.c does:
        // "Hash Join" + Left → "Hash Left Join"; "Nested Loop" + Anti → "Nested Loop Anti Join".
        if (node.JoinType is { } joinType && joinType != "Inner")
        {
            name = name == "Nested Loop"
                ? $"Nested Loop {joinType} Join"
                : name.Replace(" Join", $" {joinType} Join");
        }

        if (name == "Aggregate" && node.Strategy is { } strategy)
        {
            name = strategy switch
            {
                "Sorted" => "GroupAggregate",
                "Hashed" => "HashAggregate",
                "Mixed" => "MixedAggregate",
                _ => "Aggregate",
            };
        }

        if (node.PartialMode is { } partialMode && partialMode != "Simple")
        {
            name = $"{partialMode} {name}";
        }

        // ModifyTable displays as its operation: "Insert on orders".
        if (name == "ModifyTable" && node.Operation is { } operation)
        {
            name = operation;
        }

        if (node.ParallelAware)
        {
            name = $"Parallel {name}";
        }

        if (node.IndexName is { } index)
        {
            if (node.NodeType == "Bitmap Index Scan")
            {
                name += $" on {index}";
            }
            else
            {
                if (node.ScanDirection == "Backward")
                {
                    name += " Backward";
                }

                name += $" using {index}";
                if (Target(node) is { } indexTarget)
                {
                    name += $" on {indexTarget}";
                }
            }
        }
        else if (Target(node) is { } target)
        {
            name += $" on {target}";
        }

        return name;
    }

    /// <summary>Scan target with its alias when aliased: "orders o"; null for non-scan nodes.</summary>
    private static string? Target(ExplainNode node)
    {
        if (node.RelationName is not { } relation)
        {
            return null;
        }

        return node.Alias is { } alias && alias != relation ? $"{relation} {alias}" : relation;
    }

    private static void AppendNode(StringBuilder sb, ExplainNode node, int textIndent, bool isRoot)
    {
        var line = new StringBuilder();
        if (!isRoot)
        {
            line.Append(' ', textIndent - 4).Append("->  ");
        }

        line.Append(HeaderFor(node));
        line.Append(Invariant($"  (cost={node.StartupCost:F2}..{node.TotalCost:F2} rows={node.PlanRows} width={node.PlanWidth})"));

        if (node.ActualLoops == 0)
        {
            line.Append(" (never executed)");
        }
        else if (node.ActualRows is { } actualRows)
        {
            line.Append(" (actual");
            if (node.ActualTotalTimeMs is { } totalTime)
            {
                line.Append(Invariant($" time={node.ActualStartupTimeMs ?? 0:F3}..{totalTime:F3}"));
            }

            // "0.##" instead of PG 18's fixed two decimals: integral counts stay
            // clean ("rows=7"), fractional per-loop averages keep theirs ("rows=7.50").
            line.Append(Invariant($" rows={actualRows:0.##} loops={node.ActualLoops})"));
        }

        sb.AppendLine(line.ToString());

        var detailIndent = new string(' ', textIndent + 2);
        foreach (var (key, value) in node.Details)
        {
            // Buffer counters and I/O timings are folded into single aggregate lines
            // below (matching EXPLAIN (FORMAT TEXT)) rather than one line per counter.
            if (IsBufferBlockKey(key) || key is "I/O Read Time" or "I/O Write Time")
            {
                continue;
            }

            sb.Append(detailIndent).Append(key).Append(": ").AppendLine(value);
        }

        if (FormatBuffers(node.Details) is { } buffers)
        {
            sb.Append(detailIndent).AppendLine(buffers);
        }

        if (FormatIoTimings(node.Details) is { } ioTimings)
        {
            sb.Append(detailIndent).AppendLine(ioTimings);
        }

        foreach (var child in node.Children)
        {
            // Subplans announce themselves on their own line above the arrow,
            // at the parent's detail indent — same as text-format EXPLAIN.
            if (child.SubplanName is { } subplan)
            {
                sb.Append(detailIndent).AppendLine(subplan);
            }

            AppendNode(sb, child, textIndent + 6, isRoot: false);
        }
    }

    private static readonly string[] BufferPools = ["Shared ", "Local ", "Temp "];

    private static bool IsBufferBlockKey(string key) =>
        key.EndsWith(" Blocks", StringComparison.Ordinal)
        && Array.Exists(BufferPools, p => key.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// Folds the per-pool buffer counters into the one `Buffers:` line
    /// `EXPLAIN (FORMAT TEXT)` emits — <c>shared hit=… read=…, local …, temp read=… written=…</c> —
    /// showing only non-zero counters and only pools that have any (the parser already
    /// drops zero-valued block lines). Null when the node reports no buffer usage.
    /// </summary>
    private static string? FormatBuffers(IReadOnlyList<KeyValuePair<string, string>> details)
    {
        var groups = new List<string>();
        AddGroup(groups, details, "shared", ("hit", "Shared Hit Blocks"), ("read", "Shared Read Blocks"),
            ("dirtied", "Shared Dirtied Blocks"), ("written", "Shared Written Blocks"));
        AddGroup(groups, details, "local", ("hit", "Local Hit Blocks"), ("read", "Local Read Blocks"),
            ("dirtied", "Local Dirtied Blocks"), ("written", "Local Written Blocks"));
        AddGroup(groups, details, "temp", ("read", "Temp Read Blocks"), ("written", "Temp Written Blocks"));

        return groups.Count == 0 ? null : "Buffers: " + string.Join(", ", groups);
    }

    private static void AddGroup(
        List<string> groups,
        IReadOnlyList<KeyValuePair<string, string>> details,
        string label,
        params (string Suffix, string Key)[] members)
    {
        var parts = new List<string>();
        foreach (var (suffix, key) in members)
        {
            if (Detail(details, key) is { } value && value != "0")
            {
                parts.Add($"{suffix}={value}");
            }
        }

        if (parts.Count > 0)
        {
            groups.Add($"{label} {string.Join(" ", parts)}");
        }
    }

    /// <summary>The `I/O Timings:` line (only when track_io_timing was on), 3-decimal like Postgres.</summary>
    private static string? FormatIoTimings(IReadOnlyList<KeyValuePair<string, string>> details)
    {
        var parts = new List<string>();
        AppendIoTiming(parts, details, "read", "I/O Read Time");
        AppendIoTiming(parts, details, "write", "I/O Write Time");
        return parts.Count == 0 ? null : "I/O Timings: " + string.Join(" ", parts);
    }

    private static void AppendIoTiming(List<string> parts, IReadOnlyList<KeyValuePair<string, string>> details, string label, string key)
    {
        if (Detail(details, key) is { } value
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            && ms != 0)
        {
            parts.Add(Invariant($"{label}={ms:F3}"));
        }
    }

    private static string? Detail(IReadOnlyList<KeyValuePair<string, string>> details, string key)
    {
        foreach (var pair in details)
        {
            if (pair.Key == key)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
