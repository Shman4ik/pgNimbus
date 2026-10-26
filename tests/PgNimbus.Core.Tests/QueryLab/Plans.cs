using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>Builds small plan trees by hand, for the cases a real capture can't pin down.</summary>
internal static class Plans
{
    public static ExplainNode Node(
        string type,
        string? relation = null,
        string? alias = null,
        string? index = null,
        string? joinType = null,
        string? strategy = null,
        bool parallel = false,
        double cost = 10,
        long rows = 100,
        string? parent = null,
        (string Key, string Value)[]? details = null,
        ExplainNode[]? children = null) =>
        new(
            type,
            relation,
            alias,
            index,
            joinType,
            ScanDirection: null,
            SubplanName: null,
            strategy,
            PartialMode: null,
            Operation: null,
            parallel,
            StartupCost: 0,
            TotalCost: cost,
            PlanRows: rows,
            PlanWidth: 8,
            ActualStartupTimeMs: null,
            ActualTotalTimeMs: null,
            ActualRows: null,
            ActualLoops: null,
            [.. (details ?? []).Select(d => new KeyValuePair<string, string>(d.Key, d.Value))],
            children ?? [])
        {
            ParentRelationship = parent,
        };

    /// <summary>The node as EXPLAIN ANALYZE reports it: time and rows per loop.</summary>
    public static ExplainNode Ran(this ExplainNode node, double ms, double rows, long loops = 1) =>
        node with { ActualStartupTimeMs = 0, ActualTotalTimeMs = ms, ActualRows = rows, ActualLoops = loops };

    public static ExplainNode NeverRan(this ExplainNode node) => node with { ActualRows = 0, ActualLoops = 0 };

    public static ExplainResult Plan(ExplainNode root, double? executionMs = null, ExplainCapture? capture = null) =>
        new(root, PlanningTimeMs: 0.1, executionMs) { Capture = capture ?? ExplainCapture.Unknown };

    public static ExplainCapture Measured(bool? buffers = true, params (string Key, string Value)[] settings) =>
        new()
        {
            Format = PlanFormat.Json,
            Buffers = buffers,
            Settings = [.. settings.Select(s => new KeyValuePair<string, string>(s.Key, s.Value))],
        };

    /// <summary>Every node of a merged tree, folded rows' children included, in preorder.</summary>
    public static IEnumerable<PlanDiffNode> All(IEnumerable<PlanDiffNode> roots) =>
        roots.SelectMany(root => new[] { root }.Concat(All(root.Children)));
}
