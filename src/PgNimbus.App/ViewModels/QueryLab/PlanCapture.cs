using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// What a tab knows about the plan it shows, recorded at the moment the plan was put
/// on screen, so a Save to Query Lab keeps the statement that was explained rather than
/// whatever the editor holds by then. Cleared with the plan.
/// </summary>
/// <param name="PlanText">The raw FORMAT JSON when there is one, else the plan text: what Query Lab saves.</param>
/// <param name="Statement">The statement that was explained; null for an import.</param>
public sealed record PlanCapture(
    ExplainResult Plan,
    string PlanText,
    SavedPlanSource Source,
    string? Statement,
    string? ServerVersion,
    DateTimeOffset CapturedAt)
{
    /// <summary>The saved plan this tab was opened from, when it was.</summary>
    public SavedPlan? Saved { get; init; }
}
