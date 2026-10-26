using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.App.ViewModels;

/// <summary>A row of Query Lab's summary table: a plan-level figure on both sides.</summary>
public sealed class PlanMetricRowViewModel(PlanMetricRow row)
{
    public string Label => row.Label;

    public string BeforeText => row.BeforeText;

    public string AfterText => row.AfterText;

    public string ChangeText => row.ChangeText;

    /// <summary>A caveat beside the figure's name ("one run each").</summary>
    public string? Note => row.Note;

    public bool HasNote => row.Note is not null;

    /// <summary>Why a side has no figure ("not analyzed"), shown on hover over its dash.</summary>
    public string? BeforeTip => row.BeforeMissing;

    public string? AfterTip => row.AfterMissing;

    public bool BeforeMissing => row.Before is null;

    public bool AfterMissing => row.After is null;
}

/// <summary>A warning the analyzer raised on one side only: gone after, or new after.</summary>
public sealed class WarningChangeViewModel(PlanWarning warning, bool introduced)
{
    public string Glyph => introduced ? "⚠" : "✓";

    public string Word => introduced ? "New" : "Gone";

    public string Title => warning.Title;

    public string Detail => warning.Detail;

    public bool Introduced => introduced;
}

/// <summary>
/// One comparison as Query Lab shows it: the summary table, the notes, the settings and
/// warnings that changed, and the merged plan tree.
/// </summary>
public sealed partial class PlanComparisonViewModel : ObservableObject
{
    public PlanComparisonViewModel(PlanComparison comparison, string beforeLabel, string afterLabel)
    {
        Comparison = comparison;
        BeforeLabel = beforeLabel;
        AfterLabel = afterLabel;
        Metrics = [.. comparison.Metrics.Select(r => new PlanMetricRowViewModel(r))];
        Notes = [.. comparison.Notes.Select(n => new PlanWarningViewModel(n))];
        SettingChanges = [.. comparison.Settings.Select(s => $"{s.Name}: {s.Before ?? "default"} → {s.After ?? "default"}")];
        WarningChanges =
        [
            .. comparison.WarningsResolved.Select(w => new WarningChangeViewModel(w, introduced: false)),
            .. comparison.WarningsIntroduced.Select(w => new WarningChangeViewModel(w, introduced: true)),
        ];
        Roots = [.. comparison.Diff.Roots.Select(r => new PlanDiffNodeViewModel(r))];

        var diff = comparison.Diff;
        var parts = new List<string>();
        void Part(int count, string word)
        {
            if (count > 0)
            {
                parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count:N0} {word}"));
            }
        }

        Part(diff.Changed, "changed");
        Part(diff.Added, "added");
        Part(diff.Removed, "removed");
        Part(diff.Moved, "moved");
        ChangeSummary = parts.Count == 0 ? "Same plan: every node matches" : string.Join(" · ", parts);
    }

    public PlanComparison Comparison { get; }

    public string BeforeLabel { get; }

    public string AfterLabel { get; }

    public IReadOnlyList<PlanMetricRowViewModel> Metrics { get; }

    public IReadOnlyList<PlanWarningViewModel> Notes { get; }

    public IReadOnlyList<string> SettingChanges { get; }

    public bool HasSettingChanges => SettingChanges.Count > 0;

    public IReadOnlyList<WarningChangeViewModel> WarningChanges { get; }

    public bool HasWarningChanges => WarningChanges.Count > 0;

    public IReadOnlyList<PlanDiffNodeViewModel> Roots { get; }

    /// <summary>"1 changed · 1 added · 1 removed", above the plan tree.</summary>
    public string ChangeSummary { get; }

    /// <summary>The plan-tree row whose before and after figures the details strip shows.</summary>
    [ObservableProperty]
    private PlanDiffNodeViewModel? _selectedNode;

    public bool HasSelectedNode => SelectedNode is not null;

    partial void OnSelectedNodeChanged(PlanDiffNodeViewModel? value) => OnPropertyChanged(nameof(HasSelectedNode));
}
