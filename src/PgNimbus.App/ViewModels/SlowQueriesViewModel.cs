using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core;
using PgNimbus.Core.Localization;
using PgNimbus.Core.Monitoring;

namespace PgNimbus.App.ViewModels;

/// <summary>One ranked statement, shaped for the grid.</summary>
public sealed partial record SlowQueryRow(int Rank, StatementActivity Activity, double TimeShare)
{
    /// <summary>The statement on one line: the grid shows a preview, never a paragraph.</summary>
    public string Query => Whitespace().Replace(Activity.Statement.Query, " ").Trim();

    public string Role => Activity.Statement.Role;

    public string Calls => Activity.Calls.ToString("N0", CultureInfo.InvariantCulture);

    public string Total => DurationText.Format(Activity.TotalMs);

    public string Mean => DurationText.Format(Activity.MeanMs);

    public string Share => TimeShare.ToString("P1", CultureInfo.InvariantCulture);

    public string Rows => Activity.Rows.ToString("N0", CultureInfo.InvariantCulture);

    public string CacheHit => Activity.CacheHitRatio is { } r ? r.ToString("P1", CultureInfo.InvariantCulture) : "—";

    /// <summary>Its numbers cover only part of the interval (reset or evicted in between); marked in the grid.</summary>
    public bool Restarted => Activity.Restarted;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// The slow-query shortlist (ROADMAP Q2): pg_stat_statements for the current
/// database, ranked by where the time went. Two scopes: everything since the
/// statistics were last reset, or only what ran since a baseline, which is
/// the read taken when the window opened until "Restart interval" moves it.
/// The second answers "what did my workload just do?" without resetting the
/// server's counters for everybody else, which is why nothing here ever calls
/// <c>pg_stat_statements_reset()</c>.
///
/// Read-only like the other monitoring windows, and nothing runs from it: a
/// statement opens in a new editor tab with its <c>$1</c> placeholders intact,
/// since pg_stat_statements keeps the shape of a query, not its values.
/// </summary>
public sealed partial class SlowQueriesViewModel : ObservableObject
{
    /// <summary>How many statements the grid lists.</summary>
    public const int Limit = 100;

    private readonly StatementStatsService _service;

    private StatementStatsSnapshot? _baseline;
    private StatementStatsSnapshot? _latest;

    // What the texts below are built from, kept so a change of language can write them again.
    private StatementStatsProblem _problem;
    private StatementInterval? _interval;
    private int _activityCount;
    private bool _statusFromRead;

    public SlowQueriesViewModel(StatementStatsService service)
    {
        _service = service;
        Localizer.WhenChanged(this, static vm => vm.OnLanguageChanged());
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(ProblemTitle));
        OnPropertyChanged(nameof(ProblemDetail));
        OnPropertyChanged(nameof(BaselineLabel));
        OnPropertyChanged(nameof(EmptyText));
        if (_statusFromRead)
        {
            Status = BuildStatus();
        }
    }

    public ObservableCollection<SlowQueryRow> Rows { get; } = [];

    [ObservableProperty]
    private SlowQueryRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRankedByTotal), nameof(IsRankedByMean), nameof(IsRankedByCalls))]
    private StatementRanking _ranking = StatementRanking.TotalTime;

    public bool IsRankedByTotal => Ranking == StatementRanking.TotalTime;

    public bool IsRankedByMean => Ranking == StatementRanking.MeanTime;

    public bool IsRankedByCalls => Ranking == StatementRanking.Calls;

    /// <summary>False: since the statistics were reset. True: since the baseline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSinceReset))]
    private bool _isSinceBaseline;

    public bool IsSinceReset => !IsSinceBaseline;

    /// <summary>The baseline scope's chip: "Since 10:04:05".</summary>
    public string BaselineLabel =>
        _baseline is { } baseline ? Strings.Monitoring_SlowSince($"{Local(baseline.TakenAt):HH:mm:ss}") : Strings.Monitoring_SlowSinceOpened;

    /// <summary>Why there's nothing to show (the extension missing or not loaded), or null.</summary>
    public string? ProblemTitle => _problem switch
    {
        StatementStatsProblem.NotInstalled => Strings.Monitoring_SlowNotInstalledTitle,
        StatementStatsProblem.NotLoaded => Strings.Monitoring_SlowNotLoadedTitle,
        _ => null,
    };

    public string? ProblemDetail => _problem switch
    {
        StatementStatsProblem.NotInstalled => Strings.Monitoring_SlowNotInstalledDetail,
        StatementStatsProblem.NotLoaded => Strings.Monitoring_SlowNotLoadedDetail,
        _ => null,
    };

    public bool HasProblem => ProblemTitle is not null;

    public bool HasGrid => ProblemTitle is null;

    /// <summary>Shown over an empty grid; null when there are rows.</summary>
    public string? EmptyText => _latest is null || _baseline is null || Rows.Count > 0 ? null
        : IsSinceBaseline
            ? Strings.Monitoring_SlowEmptyInterval(ScopeText())
            : Strings.Monitoring_SlowEmptyNone;

    [ObservableProperty]
    private string _status = "";

    /// <summary>Raised with a tab title and SQL; the main window opens it in a new tab.</summary>
    public event Action<string, string>? OpenSqlRequested;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            Load(await _service.ReadAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _statusFromRead = false;
            Status = ex.Message;
        }
    }

    /// <summary>
    /// Shows a read: the problem it reports, or its statements in the current
    /// scope. The first successful read becomes the baseline. Public for the
    /// screenshot harness, which has no server behind it.
    /// </summary>
    public void Load(StatementStatsRead read)
    {
        _problem = read.Problem;
        OnPropertyChanged(nameof(ProblemTitle));
        OnPropertyChanged(nameof(ProblemDetail));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasGrid));

        if (read.Snapshot is not { } snapshot)
        {
            Rows.Clear();
            _latest = null;
            _statusFromRead = false;
            OnPropertyChanged(nameof(EmptyText));
            Status = "";
            return;
        }

        _baseline ??= snapshot;
        _latest = snapshot;
        OnPropertyChanged(nameof(BaselineLabel));
        Rebuild();
    }

    [RelayCommand]
    private void SetRanking(StatementRanking ranking)
    {
        Ranking = ranking;
        Rebuild();
    }

    [RelayCommand]
    private void SetScope(bool sinceBaseline)
    {
        IsSinceBaseline = sinceBaseline;
        Rebuild();
    }

    /// <summary>Makes the latest read the new baseline: the interval starts now.</summary>
    [RelayCommand]
    private void RestartInterval()
    {
        if (_latest is null)
        {
            return;
        }

        _baseline = _latest;
        OnPropertyChanged(nameof(BaselineLabel));
        IsSinceBaseline = true;
        Rebuild();
    }

    /// <summary>
    /// Opens a statement's whole text in a new tab, under a comment that says
    /// where it came from and that its constants are placeholders now.
    /// </summary>
    [RelayCommand]
    private async Task OpenInNewTabAsync(SlowQueryRow? row)
    {
        if ((row ?? SelectedRow) is not { } target)
        {
            return;
        }

        var text = await GetQueryTextAsync(target);
        var activity = target.Activity;
        var sql = $"""
            -- From pg_stat_statements: {activity.Calls:N0} calls, {DurationText.Format(activity.TotalMs)} in total, {DurationText.Format(activity.MeanMs)} per call ({EnglishScopeText()}).
            -- Its constants were replaced by $1, $2, …: put real values back before running it.
            {text}
            """;
        OpenSqlRequested?.Invoke(Strings.Monitoring_SlowTabTitle(target.Rank), sql);
    }

    /// <summary>A statement's whole text; falls back to the preview when the entry is gone.</summary>
    public async Task<string> GetQueryTextAsync(SlowQueryRow row)
    {
        try
        {
            return await _service.GetQueryTextAsync(row.Activity.Statement, CancellationToken.None) ?? row.Activity.Statement.Query;
        }
        catch (Exception)
        {
            return row.Activity.Statement.Query;
        }
    }

    /// <summary>The two setup steps as a script to review, never run from here.</summary>
    [RelayCommand]
    private void OpenSetupScript() => OpenSqlRequested?.Invoke(Strings.Monitoring_SlowSetupTabTitle, SetupScript);

    internal const string SetupScript = """
        -- Setting up pg_stat_statements. Both steps need a superuser; review before running.

        -- 1. Load the library when the server starts, then restart the server.
        --    On a managed service (RDS, Cloud SQL, Azure …) set it in the parameter group instead.
        SHOW shared_preload_libraries;
        --    The line below is commented out on purpose. It REPLACES the whole list,
        --    so running this tab as it stands would unload every other library shown
        --    above. Add those names to it, keep pg_stat_statements, then uncomment it.
        -- ALTER SYSTEM SET shared_preload_libraries = 'pg_stat_statements';

        -- 2. After the restart, in each database you want to look at:
        CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
        """;

    private void Rebuild()
    {
        if (_latest is not { } latest || _baseline is not { } baseline)
        {
            return;
        }

        StatementInterval? interval = IsSinceBaseline ? StatementStatsInterval.Between(baseline, latest) : null;
        IReadOnlyCollection<StatementActivity> activity = interval?.Activity
            ?? [.. latest.Statements.Where(s => s.Calls > 0).Select(s => StatementActivity.Cumulative(s))];

        var ranked = StatementStatsInterval.Rank(activity, Ranking, Limit);
        Rows.Clear();
        for (var i = 0; i < ranked.Count; i++)
        {
            Rows.Add(new SlowQueryRow(i + 1, ranked[i].Activity, ranked[i].TimeShare));
        }

        _interval = interval;
        _activityCount = activity.Count;
        _statusFromRead = true;
        OnPropertyChanged(nameof(EmptyText));
        Status = BuildStatus();
    }

    private string BuildStatus()
    {
        var latest = _latest!;
        var notes = new List<string>
        {
            _activityCount > Rows.Count
                ? Strings.Monitoring_SlowStatementsTop(Rows.Count, _activityCount.ToString("N0"), ScopeText())
                : Strings.Monitoring_SlowStatements(_activityCount, ScopeText()),
        };

        if (_interval is { ViewReset: true })
        {
            notes.Add(Strings.Monitoring_SlowNoteViewReset);
        }

        if (_interval?.Activity.Count(a => a.Restarted) is > 0 and var restarted)
        {
            notes.Add(Strings.Monitoring_SlowNoteRestarted(restarted));
        }

        if (_interval?.Evicted is > 0 and var evicted)
        {
            notes.Add(Strings.Monitoring_SlowNoteEvicted(evicted.ToString("N0")));
        }

        if (latest.HiddenStatements > 0)
        {
            notes.Add(Strings.Monitoring_SlowNoteHidden(latest.HiddenStatements.ToString("N0")));
        }

        if (latest.OwnStatements > 0)
        {
            notes.Add(Strings.Monitoring_SlowNoteOwn(latest.OwnStatements.ToString("N0")));
        }

        notes.Add(Strings.Monitoring_SlowNoteRead($"{Local(latest.TakenAt):HH:mm:ss}"));
        return string.Join(" · ", notes);
    }

    private string ScopeText()
    {
        if (IsSinceBaseline && _baseline is { } baseline && _latest is { } latest)
        {
            return Strings.Monitoring_SlowScopeBetween($"{Local(baseline.TakenAt):HH:mm:ss}", $"{Local(latest.TakenAt):HH:mm:ss}");
        }

        return _latest?.StatsReset is { } reset
            ? Strings.Monitoring_SlowScopeSinceReset($"{Local(reset):yyyy-MM-dd HH:mm}")
            : Strings.Monitoring_SlowScopeSinceStart;
    }

    // The same period in English, for the comment at the top of a statement opened in
    // a tab: generated SQL and its comments are not translated.
    private string EnglishScopeText()
    {
        if (IsSinceBaseline && _baseline is { } baseline && _latest is { } latest)
        {
            return $"between {Local(baseline.TakenAt):HH:mm:ss} and {Local(latest.TakenAt):HH:mm:ss}";
        }

        return _latest?.StatsReset is { } reset
            ? $"since the statistics were reset on {Local(reset):yyyy-MM-dd HH:mm}"
            : "since the server started counting";
    }

    private static DateTime Local(DateTime time) =>
        time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
}
