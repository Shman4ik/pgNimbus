using PgNimbus.Core.Query;

namespace PgNimbus.Core.QueryLab;

/// <summary>What kind of work a plan node does, for pairing nodes whose type changed between two plans.</summary>
public enum PlanNodeFamily
{
    Other,
    Scan,
    Bitmap,
    Join,
    Sort,
    Aggregate,
    Window,
    Gather,
    Append,
    SetOp,
    Cache,
    Hash,
    Limit,
    Unique,
    Modify,
    Result,
    LockRows,
}

/// <summary>
/// A plan node's identity in one canonical spelling, whichever EXPLAIN format it was
/// read from. FORMAT JSON names a node by its type plus fields ("Hash Join" with join
/// type Left, "Aggregate" with strategy Hashed and partial mode Partial, "ModifyTable"
/// with operation Insert); FORMAT TEXT folds them into one name ("Hash Left Join",
/// "Partial HashAggregate", "Insert on t"), which the text parser keeps whole. Two
/// labels are equal exactly when the nodes do the same thing to the same relation, so
/// a JSON plan and its text twin compare as identical.
/// </summary>
public sealed record PlanNodeLabel(
    string Type,
    string? JoinType,
    string? Strategy,
    string? PartialMode,
    string? Operation,
    string? Relation,
    string? Alias,
    string? Index,
    string? Direction,
    string? Subplan,
    bool Parallel)
{
    public PlanNodeFamily Family => FamilyOf(Type);

    /// <summary>True for a node that reads a relation, a function or a CTE by name.</summary>
    public bool IsScanOfRelation => Relation is not null;

    /// <param name="format">
    /// The format the plan was read from: only a text plan quotes names and prefixes a
    /// schema, and a name in FORMAT JSON is the name, dots and quotes included.
    /// </param>
    public static PlanNodeLabel Of(ExplainNode node, PlanFormat format = PlanFormat.Unknown)
    {
        var text = format == PlanFormat.Text;
        var type = node.NodeType;
        var joinType = node.JoinType;
        var strategy = node.Strategy;
        var partialMode = node.PartialMode;
        var operation = node.Operation;

        // "Partial HashAggregate", "Finalize GroupAggregate" (text only; JSON has Partial Mode).
        foreach (var mode in (ReadOnlySpan<string>)["Partial ", "Finalize "])
        {
            if (type.StartsWith(mode, StringComparison.Ordinal))
            {
                partialMode ??= mode.TrimEnd();
                type = type[mode.Length..];
            }
        }

        switch (type)
        {
            case "GroupAggregate":
                (type, strategy) = ("Aggregate", strategy ?? "Sorted");
                break;
            case "HashAggregate":
                (type, strategy) = ("Aggregate", strategy ?? "Hashed");
                break;
            case "MixedAggregate":
                (type, strategy) = ("Aggregate", strategy ?? "Mixed");
                break;
            case "Insert" or "Update" or "Delete" or "Merge":
                (type, operation) = ("ModifyTable", operation ?? type);
                break;
        }

        if (type.StartsWith("HashSetOp", StringComparison.Ordinal))
        {
            (type, strategy) = ("SetOp", strategy ?? "Hashed");
        }
        else if (type.StartsWith("SetOp ", StringComparison.Ordinal))
        {
            (type, strategy) = ("SetOp", strategy ?? "Sorted");
        }

        // "Hash Left Join" → Hash Join + Left; "Nested Loop Anti Join" → Nested Loop + Anti.
        if (FoldedJoin(type) is { } folded)
        {
            (type, joinType) = (folded.Type, joinType ?? folded.JoinType);
        }

        if (FamilyOf(type) == PlanNodeFamily.Join)
        {
            joinType ??= "Inner";
        }

        if (type == "Aggregate")
        {
            strategy ??= "Plain";
        }

        if (partialMode == "Simple")
        {
            partialMode = null;
        }

        // A Subquery Scan or Values Scan has only an alias in FORMAT JSON, where the text
        // format writes that alias as the scan's target ("Subquery Scan on sub").
        var relation = Name(node.RelationName, text)
            ?? (FamilyOf(type) == PlanNodeFamily.Scan ? Name(node.Alias, text) : null);
        var alias = Name(node.Alias, text) ?? relation;
        return new PlanNodeLabel(
            type,
            joinType,
            strategy,
            partialMode,
            operation,
            relation,
            relation is null ? null : alias,
            node.IndexName,
            node.ScanDirection == "Backward" ? "Backward" : null,
            SubplanKey(node.SubplanName),
            node.ParallelAware);
    }

    /// <summary>
    /// A relation or alias as FORMAT JSON spells it. The text format quotes a name that
    /// needs it (<c>"*VALUES*"</c>, <c>"Order Items"</c>) and a VERBOSE text plan prefixes
    /// the schema (<c>public.orders</c>), which JSON names separately. A name that is
    /// quoted only in part (<c>app."Order Items"</c>) is left as it is.
    /// </summary>
    private static string? Name(string? name, bool text)
    {
        if (name is null || !text)
        {
            return name;
        }

        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"'
            && !name[1..^1].Replace("\"\"", "", StringComparison.Ordinal).Contains('"', StringComparison.Ordinal))
        {
            return name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        if (name.Contains('"', StringComparison.Ordinal))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    /// <summary>"InitPlan 1 (returns $0)" and PostgreSQL 18's "InitPlan 1" are the same subplan.</summary>
    private static string? SubplanKey(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    private static (string Type, string JoinType)? FoldedJoin(string type)
    {
        foreach (var method in (ReadOnlySpan<string>)["Hash ", "Merge "])
        {
            if (type.StartsWith(method, StringComparison.Ordinal) && type.EndsWith(" Join", StringComparison.Ordinal)
                && type.Length > method.Length + " Join".Length)
            {
                var join = type[method.Length..^" Join".Length];
                return ($"{method}Join", join);
            }
        }

        if (type.StartsWith("Nested Loop ", StringComparison.Ordinal) && type.EndsWith(" Join", StringComparison.Ordinal))
        {
            return ("Nested Loop", type["Nested Loop ".Length..^" Join".Length]);
        }

        return null;
    }

    public static PlanNodeFamily FamilyOf(string type) => type switch
    {
        "Seq Scan" or "Index Scan" or "Index Only Scan" or "Bitmap Heap Scan" or "Tid Scan" or "Tid Range Scan"
            or "Sample Scan" or "Function Scan" or "Table Function Scan" or "Values Scan" or "CTE Scan"
            or "Named Tuplestore Scan" or "WorkTable Scan" or "Subquery Scan" or "Foreign Scan" => PlanNodeFamily.Scan,
        _ when type.StartsWith("Custom Scan", StringComparison.Ordinal) => PlanNodeFamily.Scan,
        "Bitmap Index Scan" or "BitmapAnd" or "BitmapOr" => PlanNodeFamily.Bitmap,
        "Nested Loop" or "Hash Join" or "Merge Join" => PlanNodeFamily.Join,
        "Sort" or "Incremental Sort" => PlanNodeFamily.Sort,
        "Aggregate" or "Group" => PlanNodeFamily.Aggregate,
        "WindowAgg" => PlanNodeFamily.Window,
        "Gather" or "Gather Merge" => PlanNodeFamily.Gather,
        "Append" or "Merge Append" => PlanNodeFamily.Append,
        "SetOp" or "Recursive Union" => PlanNodeFamily.SetOp,
        "Materialize" or "Memoize" => PlanNodeFamily.Cache,
        "Hash" => PlanNodeFamily.Hash,
        "Limit" => PlanNodeFamily.Limit,
        "Unique" => PlanNodeFamily.Unique,
        "ModifyTable" => PlanNodeFamily.Modify,
        "Result" or "ProjectSet" => PlanNodeFamily.Result,
        "LockRows" => PlanNodeFamily.LockRows,
        _ => PlanNodeFamily.Other,
    };
}
