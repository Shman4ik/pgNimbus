using System.Collections.Concurrent;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.App.Converters;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.App.ViewModels;

/// <summary>A saved plan as Query Lab's list shows it.</summary>
public sealed class SavedPlanItem(SavedPlan plan)
{
    public SavedPlan Plan { get; } = plan;

    public string Name => Plan.Name;

    /// <summary>"09:41 · 13.4 ms · PG 17.11", "Imported · estimates only".</summary>
    public string Detail => Describe(Plan);

    /// <summary>Where it came from and the statement, on hover.</summary>
    public string Tip
    {
        get
        {
            var lines = new List<string>
            {
                Plan.CapturedAt.ToOffset(HistoryText.Now().Offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            };
            lines.Add(Plan.Source switch
            {
                SavedPlanSource.Explain => "From the Explain command",
                SavedPlanSource.Run => "From an EXPLAIN that was run",
                _ => "Imported",
            });
            if (Plan.Connection is { } connection)
            {
                lines.Add(connection);
            }

            if (Plan.Statement is { } statement)
            {
                lines.Add(HistoryLabel.Tip(statement, maxLines: 12, maxLength: 1200));
            }

            return string.Join('\n', lines);
        }
    }

    internal static string Describe(SavedPlan plan)
    {
        var parts = new List<string> { plan.Source == SavedPlanSource.Imported ? "Imported" : HistoryLabel.When(plan.CapturedAt, HistoryText.Now()) };
        if (plan.Summary is { Analyzed: true, ExecutionMs: { } ms })
        {
            parts.Add(PlanMetricRow.Format(ms, PlanMetricUnit.Milliseconds));
        }
        else if (plan.Summary is { Analyzed: false })
        {
            parts.Add("estimates only");
        }

        if (plan.ServerVersion is { } version)
        {
            parts.Add("PG " + version);
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>A side Query Lab can compare: a saved plan, or the plan a tab is showing that nobody saved.</summary>
public sealed class PlanChoice
{
    private PlanChoice(SavedPlan? saved, PlanCapture? capture, string label, string detail)
    {
        Saved = saved;
        Capture = capture;
        Label = label;
        Detail = detail;
    }

    public SavedPlan? Saved { get; }

    public PlanCapture? Capture { get; }

    public string Label { get; }

    public string Detail { get; }

    public bool IsUnsaved => Saved is null;

    public static PlanChoice ForSaved(SavedPlan saved) => new(saved, null, saved.Name, SavedPlanItem.Describe(saved));

    public static PlanChoice ForCapture(PlanCapture capture, string tabTitle) =>
        new(null, capture, $"Unsaved: the plan in “{tabTitle}”",
            capture.Plan.ExecutionTimeMs is { } ms ? PlanMetricRow.Format(ms, PlanMetricUnit.Milliseconds) : "estimates only");
}

/// <summary>
/// Query Lab: the saved plans, and a comparison of any two of them or of one and the
/// plan a tab is showing. One per main window, like Slow Queries; nothing runs from it,
/// and it needs no connection, since every plan it compares is already parsed text.
/// </summary>
public sealed partial class QueryLabViewModel : ObservableObject
{
    private readonly SavedPlanStore _store;
    private readonly Func<string?> _connection;

    // Parsed saved plans, by id: a plan's file is read and parsed once per window.
    private readonly ConcurrentDictionary<Guid, ExplainResult> _parsed = new();

    private PlanChoice? _unsaved;
    private CancellationTokenSource? _compare;
    private bool _rebuilding;

    public QueryLabViewModel(SavedPlanStore store, Func<string?> connection)
    {
        _store = store;
        _connection = connection;
    }

    /// <summary>The saved plans, newest first.</summary>
    public RangeObservableCollection<SavedPlanItem> Plans { get; } = [];

    /// <summary>What the Before and After pickers offer: the unsaved plan handed over, if any, then every saved one.</summary>
    public RangeObservableCollection<PlanChoice> Choices { get; } = [];

    [ObservableProperty]
    private SavedPlanItem? _selectedPlan;

    [ObservableProperty]
    private PlanChoice? _before;

    [ObservableProperty]
    private PlanChoice? _after;

    [ObservableProperty]
    private PlanComparisonViewModel? _comparison;

    [ObservableProperty]
    private bool _isComparing;

    [ObservableProperty]
    private string? _status;

    /// <summary>The Summary (0) or Plan changes (1) tab.</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>
    /// How store reads and writes and the comparison leave the UI thread (UI-thread rule
    /// d): the thread pool. The screenshot harness runs them inline, so the frame it
    /// captures holds their result.
    /// </summary>
    public Func<Action, Task> OffUiThread { get; set; } = work => Task.Run(work);

    /// <summary>The comparison under way, for a test to await.</summary>
    public Task PendingComparison { get; private set; } = Task.CompletedTask;

    public bool HasPlans => Plans.Count > 0;

    /// <summary>The pickers have something to offer.</summary>
    public bool HasChoices => Choices.Count > 0;

    public bool HasComparison => Comparison is not null;

    /// <summary>What the right-hand side says while there is nothing to compare.</summary>
    public string EmptyText =>
        Choices.Count == 0
            ? "No saved plans yet. Show a plan in a tab with Explain, Explain Analyze or Import query plan, then choose Save to Query Lab in its Compare menu. Import Plan here adds one without a connection."
            : Before is null || After is null
                ? "Pick a plan for each side to compare them."
                : ReferenceEquals(Before, After)
                    ? "Pick two different plans."
                    : IsComparing ? "Comparing…" : string.Empty;

    /// <summary>Asks for the plan text to import (the view shows <c>ImportPlanDialog</c>); null when cancelled.</summary>
    public Func<Task<ImportedPlan?>>? PickImport { get; set; }

    /// <summary>
    /// Asks for a name (the view shows the naming dialog): the heading, the suggested name
    /// and the plan being renamed; null when cancelled, else the name and the plan it replaces.
    /// </summary>
    public Func<string, string, Guid?, Task<(string Name, Guid? ReplaceId)?>>? AskName { get; set; }

    /// <summary>Asks before a delete; false keeps the plan.</summary>
    public Func<SavedPlan, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>Raised to open a saved plan in a new tab.</summary>
    public event Action<SavedPlan, ImportedPlan>? OpenPlanRequested;

    /// <summary>The saved plan with this name, ignoring case, for the naming dialog's collision check.</summary>
    public SavedPlan? FindByName(string name) =>
        Plans.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Plan;

    /// <summary>The most recent saved plans, for the plan header's Compare With menu. Reads the small index file.</summary>
    public IReadOnlyList<SavedPlan> RecentPlans(int count) =>
        [.. _store.Load().OrderByDescending(p => p.CapturedAt).Take(count)];

    /// <summary>Reads the saved plans again, off the UI thread, keeping the pickers' choices.</summary>
    public async Task ReloadAsync()
    {
        List<SavedPlan> plans = [];
        await OffUiThread(() => plans = [.. _store.Load().OrderByDescending(p => p.CapturedAt)]);
        ApplyPlans(plans);
    }

    private void ApplyPlans(IReadOnlyList<SavedPlan> plans)
    {
        var selected = SelectedPlan?.Plan.Id;
        var before = Before;
        var after = After;
        _rebuilding = true;
        try
        {
            Plans.ReplaceAll(plans.Select(p => new SavedPlanItem(p)));
            var choices = new List<PlanChoice>();
            if (_unsaved is not null)
            {
                choices.Add(_unsaved);
            }

            choices.AddRange(plans.Select(PlanChoice.ForSaved));
            Choices.ReplaceAll(choices);
            SelectedPlan = selected is { } id ? Plans.FirstOrDefault(p => p.Plan.Id == id) : null;
            Before = Same(before);
            After = Same(after);
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(HasPlans));
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(EmptyText));
        StartComparison();
    }

    /// <summary>The choice that stands for the same plan in the rebuilt list.</summary>
    private PlanChoice? Same(PlanChoice? choice) => choice switch
    {
        null => null,
        { IsUnsaved: true } => Choices.FirstOrDefault(c => ReferenceEquals(c, choice)),
        _ => Choices.FirstOrDefault(c => c.Saved?.Id == choice.Saved!.Id),
    };

    /// <summary>
    /// Compares a tab's plan (After) with a saved one (Before), or with nothing picked yet
    /// when <paramref name="before"/> is null. A tab showing a saved plan is that saved plan.
    /// </summary>
    public async Task CompareAsync(SavedPlan? before, PlanCapture after, string tabTitle)
    {
        _unsaved = after.Saved is null ? PlanChoice.ForCapture(after, tabTitle) : null;
        await ReloadAsync();
        _rebuilding = true;
        try
        {
            After = after.Saved is { } saved ? Choices.FirstOrDefault(c => c.Saved?.Id == saved.Id) : _unsaved;
            Before = before is null ? null : Choices.FirstOrDefault(c => c.Saved?.Id == before.Id);
        }
        finally
        {
            _rebuilding = false;
        }

        SelectedTab = 0;
        StartComparison();
    }

    /// <summary>Saves a tab's plan under a name, replacing <paramref name="replaceId"/> when given.</summary>
    public async Task<SavedPlan?> SaveAsync(PlanCapture capture, string name, Guid? replaceId)
    {
        var saved = new SavedPlan(
            replaceId ?? Guid.NewGuid(),
            name,
            capture.CapturedAt,
            capture.Source,
            capture.Statement,
            capture.ServerVersion,
            capture.Saved?.Connection ?? (capture.Source == SavedPlanSource.Imported ? null : _connection()),
            SavedPlanSummary.Of(capture.Plan));
        try
        {
            await OffUiThread(() => _store.Save(saved, capture.PlanText));
        }
        catch (SavedPlanStoreException e)
        {
            Status = e.Message;
            return null;
        }

        _parsed[saved.Id] = capture.Plan;
        if (_unsaved?.Capture == capture)
        {
            // The unsaved side is now this saved plan.
            _unsaved = null;
        }

        await ReloadAsync();
        if (After is null && Choices.FirstOrDefault(c => c.Saved?.Id == saved.Id) is { } now)
        {
            After = now;
        }

        Status = $"Saved “{name}” to Query Lab";
        return saved;
    }

    [RelayCommand]
    private void Swap()
    {
        _rebuilding = true;
        try
        {
            (Before, After) = (After, Before);
        }
        finally
        {
            _rebuilding = false;
        }

        StartComparison();
    }

    [RelayCommand]
    private void CompareAsBefore(SavedPlanItem? item)
    {
        if (item is not null && Choices.FirstOrDefault(c => c.Saved?.Id == item.Plan.Id) is { } choice)
        {
            Before = choice;
        }
    }

    [RelayCommand]
    private void CompareAsAfter(SavedPlanItem? item)
    {
        if (item is not null && Choices.FirstOrDefault(c => c.Saved?.Id == item.Plan.Id) is { } choice)
        {
            After = choice;
        }
    }

    [RelayCommand]
    private async Task ImportPlanAsync()
    {
        if (PickImport is null || AskName is null || await PickImport() is not { } plan)
        {
            return;
        }

        var name = SaveQueryNames.Suggest("Imported plan", n => FindByName(n) is not null);
        if (await AskName("Save the imported plan", name, null) is not { } named)
        {
            return;
        }

        var capture = new PlanCapture(plan.Result, plan.RawJson ?? plan.DisplayText, SavedPlanSource.Imported, null, null, HistoryText.Now());
        await SaveAsync(capture, named.Name, named.ReplaceId);
    }

    [RelayCommand]
    private async Task RenameAsync(SavedPlanItem? item)
    {
        if (item is null || AskName is null || await AskName("Rename saved plan", item.Name, item.Plan.Id) is not { } named)
        {
            return;
        }

        try
        {
            await OffUiThread(() => _store.Rename(item.Plan.Id, named.Name));
        }
        catch (SavedPlanStoreException e)
        {
            Status = e.Message;
            return;
        }

        await ReloadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(SavedPlanItem? item)
    {
        if (item is null || ConfirmDelete is null || !await ConfirmDelete(item.Plan))
        {
            return;
        }

        try
        {
            await OffUiThread(() => _store.Delete(item.Plan.Id));
        }
        catch (SavedPlanStoreException e)
        {
            Status = e.Message;
            return;
        }

        _parsed.TryRemove(item.Plan.Id, out _);
        Status = $"Deleted “{item.Name}”";
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task OpenInNewTabAsync(SavedPlanItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            ImportedPlan? plan = null;
            await OffUiThread(() => plan = ImportSaved(item.Plan));
            OpenPlanRequested?.Invoke(item.Plan, plan!);
        }
        catch (FormatException e)
        {
            Status = e.Message;
        }
    }

    partial void OnBeforeChanged(PlanChoice? value) => SideChanged();

    partial void OnAfterChanged(PlanChoice? value) => SideChanged();

    partial void OnComparisonChanged(PlanComparisonViewModel? value) => OnPropertyChanged(nameof(HasComparison));

    partial void OnIsComparingChanged(bool value) => OnPropertyChanged(nameof(EmptyText));

    private void SideChanged()
    {
        OnPropertyChanged(nameof(EmptyText));
        if (!_rebuilding)
        {
            StartComparison();
        }
    }

    /// <summary>Compares the two sides off the UI thread, dropping a comparison a later pick made stale.</summary>
    private void StartComparison()
    {
        _compare?.Cancel();
        var before = Before;
        var after = After;
        if (before is null || after is null || ReferenceEquals(before, after))
        {
            Comparison = null;
            IsComparing = false;
            PendingComparison = Task.CompletedTask;
            return;
        }

        var cts = new CancellationTokenSource();
        _compare = cts;
        IsComparing = true;
        PendingComparison = RunComparisonAsync(before, after, cts);
    }

    private async Task RunComparisonAsync(PlanChoice before, PlanChoice after, CancellationTokenSource cts)
    {
        try
        {
            PlanComparison? comparison = null;
            await OffUiThread(() => comparison = PlanComparison.Compare(Side(before), Side(after)));
            if (!cts.IsCancellationRequested && comparison is not null)
            {
                Comparison = new PlanComparisonViewModel(comparison, before.Label, after.Label);
                Status = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (FormatException e)
        {
            if (!cts.IsCancellationRequested)
            {
                Comparison = null;
                Status = e.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_compare, cts))
            {
                IsComparing = false;
            }
        }
    }

    /// <summary>A side of the comparison, read and parsed when it is a saved plan. Runs on the thread pool.</summary>
    private PlanSide Side(PlanChoice choice)
    {
        if (choice.Capture is { } capture)
        {
            return new PlanSide(
                capture.Plan,
                capture.ServerVersion,
                capture.Source == SavedPlanSource.Imported ? null : _connection(),
                capture.Statement);
        }

        var saved = choice.Saved!;
        return new PlanSide(Parsed(saved), saved.ServerVersion, saved.Connection, saved.Statement);
    }

    private ExplainResult Parsed(SavedPlan saved) =>
        _parsed.TryGetValue(saved.Id, out var parsed) ? parsed : _parsed[saved.Id] = ImportSaved(saved).Result;

    private ImportedPlan ImportSaved(SavedPlan saved) =>
        _store.ReadPlan(saved.Id) is { } text
            ? ExplainService.Import(text)
            : throw new FormatException($"The plan saved as “{saved.Name}” is missing from Query Lab's folder.");
}
