using PgNimbus.Core.Query;

namespace PgNimbus.Core.QueryLab;

/// <summary>
/// Where a saved plan came from. Persisted as a number: append, never reorder.
/// </summary>
public enum SavedPlanSource
{
    /// <summary>The Explain or Explain Analyze command, which pgNimbus ran inside a rolled-back transaction.</summary>
    Explain,

    /// <summary>An EXPLAIN typed into the editor and run as written.</summary>
    Run,

    /// <summary>A plan pasted into Import query plan, from another tool or another machine.</summary>
    Imported,
}

/// <summary>
/// The figures a saved plan's list row shows, kept beside it so the list opens without
/// reading every plan. A cache of the plan itself: <see cref="Of"/> derives it again.
/// </summary>
public sealed record SavedPlanSummary(bool Analyzed, double? ExecutionMs, int Nodes)
{
    public static SavedPlanSummary Of(ExplainResult plan) =>
        new(plan.Root.ActualLoops is not null || plan.ExecutionTimeMs is not null, plan.ExecutionTimeMs, CountNodes(plan.Root));

    private static int CountNodes(ExplainNode node) => 1 + node.Children.Sum(CountNodes);
}

/// <summary>
/// A plan kept in Query Lab under a name. The plan itself is stored apart from this
/// record (<see cref="SavedPlanStore.ReadPlan"/>), as the raw FORMAT JSON or text that
/// <see cref="ExplainService.Import"/> reads, so what it measured (ANALYZE, BUFFERS,
/// settings) is read from the plan and never stored twice, and a parser fix reaches
/// plans saved before it.
/// </summary>
/// <param name="Statement">The statement that was explained, when pgNimbus knows it (not for an import).</param>
/// <param name="ServerVersion">The PostgreSQL version that produced the plan ("17.2"), when known.</param>
/// <param name="Connection">Where the plan ran, as <c>host/database</c>, when known.</param>
public sealed record SavedPlan(
    Guid Id,
    string Name,
    DateTimeOffset CapturedAt,
    SavedPlanSource Source,
    string? Statement = null,
    string? ServerVersion = null,
    string? Connection = null,
    SavedPlanSummary? Summary = null);
