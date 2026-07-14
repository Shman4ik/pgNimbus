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
            sb.Append(detailIndent).Append(key).Append(": ").AppendLine(value);
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

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
