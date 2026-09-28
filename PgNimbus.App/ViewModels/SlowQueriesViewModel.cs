using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core;
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
public sealed partial class SlowQueriesViewModel(StatementStatsService service) : ObservableObject
{
    /// <summary>How many statements the grid lists.</summary>
    public const int Limit = 100;

    private readonly StatementStatsService _service = service;

    private StatementStatsSnapshot? _baseline;
    private StatementStatsSnapshot? _latest;

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
    [ObservableProperty]
    private string _baselineLabel = "Since opened";

    /// <summary>Why there's nothing to show (the extension missing or not loaded), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(HasGrid))]
    private string? _problemTitle;

    [ObservableProperty]
    private string? _problemDetail;

    public bool HasProblem => ProblemTitle is not null;

    public bool HasGrid => ProblemTitle is null;

    /// <summary>Shown over an empty grid; null when there are rows.</summary>
    [ObservableProperty]
    private string? _emptyText;

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
        ProblemTitle = read.Problem switch
        {
            StatementStatsProblem.NotInstalled => "pg_stat_statements isn't installed in this database",
            StatementStatsProblem.NotLoaded => "pg_stat_statements is installed but the server hasn't loaded it",
            _ => null,
        };
        ProblemDetail = read.Problem switch
        {
            StatementStatsProblem.NotInstalled =>
                "It ships with PostgreSQL but has to be switched on. Add pg_stat_statements to shared_preload_libraries "
                + "and restart the server, then run CREATE EXTENSION pg_stat_statements in this database. Both need a "
                + "superuser; on a managed service the first is usually a parameter-group setting. pgNimbus won't do either for you.",
            StatementStatsProblem.NotLoaded =>
                "The server wasn't started with pg_stat_statements in shared_preload_libraries, so it has recorded "
                + "nothing. Add it there and restart the server.",
            _ => null,
        };

        if (read.Snapshot is not { } snapshot)
        {
            Rows.Clear();
            EmptyText = null;
            Status = "";
            return;
        }

        _baseline ??= snapshot;
        _latest = snapshot;
        BaselineLabel = $"Since {Local(_baseline.TakenAt):HH:mm:ss}";
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
        BaselineLabel = $"Since {Local(_baseline.TakenAt):HH:mm:ss}";
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
            -- From pg_stat_statements: {activity.Calls:N0} calls, {DurationText.Format(activity.TotalMs)} in total, {DurationText.Format(activity.MeanMs)} per call ({ScopeText()}).
            -- Its constants were replaced by $1, $2, …: put real values back before running it.
            {text}
            """;
        OpenSqlRequested?.Invoke($"slow query {target.Rank}", sql);
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
    private void OpenSetupScript() => OpenSqlRequested?.Invoke("pg_stat_statements setup", SetupScript);

    internal const string SetupScript = """
        -- Setting up pg_stat_statements. Both steps need a superuser; review before running.

        -- 1. Load the library when the server starts, then restart the server.
        --    This REPLACES the whole list: check what's there first and keep it.
        --    On a managed service (RDS, Cloud SQL, Azure …) set it in the parameter group instead.
        SHOW shared_preload_libraries;
        ALTER SYSTEM SET shared_preload_libraries = 'pg_stat_statements';

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

        EmptyText = Rows.Count > 0 ? null
            : IsSinceBaseline
                ? $"Nothing ran {ScopeText()}. Run your workload, then Refresh."
                : "No statements recorded for this database yet.";

        var notes = new List<string>
        {
            activity.Count > Rows.Count
                ? $"Top {Rows.Count} of {activity.Count:N0} statements {ScopeText()}"
                : $"{activity.Count:N0} statement{(activity.Count == 1 ? "" : "s")} {ScopeText()}",
        };

        if (interval is { ViewReset: true })
        {
            notes.Add("the statistics were reset in between, so this counts from that reset");
        }

        if (interval?.Activity.Count(a => a.Restarted) is > 0 and var restarted)
        {
            notes.Add($"{restarted} started counting again in between (marked ↺)");
        }

        if (interval?.Evicted is > 0 and var evicted)
        {
            notes.Add($"{evicted:N0} entries were evicted in between (pg_stat_statements.max), so some may be missing");
        }

        if (latest.HiddenStatements > 0)
        {
            notes.Add($"{latest.HiddenStatements:N0} statements of other roles are hidden (needs pg_read_all_stats)");
        }

        if (latest.OwnStatements > 0)
        {
            notes.Add($"{latest.OwnStatements:N0} of pgNimbus's own catalog reads left out");
        }

        notes.Add($"read {Local(latest.TakenAt):HH:mm:ss}");
        Status = string.Join(" · ", notes);
    }

    private string ScopeText()
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
