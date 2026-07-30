using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Monitoring;

namespace PgNimbus.App.ViewModels;

/// <summary>Shared formatting for the activity + blocking views.</summary>
internal static class ActivityFormat
{
    /// <summary>Human elapsed time from a query_start delta; "" for a non-positive span.</summary>
    public static string Elapsed(double seconds)
    {
        if (seconds <= 0)
        {
            return "";
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : time.TotalMinutes >= 1
                ? $"{time.Minutes}:{time.Seconds:00}"
                : $"{time.TotalSeconds:0.0}s";
    }

    /// <summary>Collapse multi-line SQL to one line so it never blows out a row's height.</summary>
    public static string OneLine(string query) =>
        string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>A pg_stat_activity row shaped for the grid: formatted elapsed time, lock-wait flag, wait label.</summary>
public sealed record ActivityRow(BackendActivity Backend)
{
    public int Pid => Backend.Pid;

    public string User => Backend.User ?? "";

    public string Database => Backend.Database ?? "";

    public string Application => Backend.Application ?? "";

    public string Client => Backend.ClientAddress ?? "local";

    public string State => Backend.State;

    public bool IsWaitingOnLock => Backend.IsWaitingOnLock;

    public string Wait => Backend.WaitEventType is null ? "" : $"{Backend.WaitEventType}: {Backend.WaitEvent}";

    public string Elapsed => Backend.State.Length == 0 ? "" : ActivityFormat.Elapsed(Backend.ElapsedSeconds);

    /// <summary>Single-line for the grid row — multi-line SQL would blow the row height out.</summary>
    public string Query => ActivityFormat.OneLine(Backend.Query);
}

/// <summary>
/// One node in the who-blocks-whom tree, wrapping a <see cref="BlockingTreeNode"/>
/// with display-ready fields. A node is either a lock <em>holder</em> (something
/// waits behind it — <see cref="IsBlocker"/>) and/or a <em>waiter</em>
/// (<see cref="IsBlocked"/>); a root holding a lock is the one to cancel to
/// unstick its whole subtree.
/// </summary>
public sealed class BlockingNode
{
    private readonly BlockingTreeNode _node;

    public BlockingNode(BlockingTreeNode node)
    {
        _node = node;
        Children = node.Children.Select(c => new BlockingNode(c)).ToList();
    }

    public int Pid => _node.Backend.Pid;

    public string Identity => $"{_node.Backend.User ?? "?"}@{_node.Backend.Database ?? "?"}";

    public bool IsBlocked => _node.Backend.BlockedByPids.Count > 0;

    public bool IsBlocker => Children.Count > 0;

    /// <summary>"waiting for RowExclusiveLock on orders" — only when this node is itself blocked.</summary>
    public string WaitLabel
    {
        get
        {
            if (!IsBlocked)
            {
                return "";
            }

            var mode = _node.Backend.LockMode;
            var obj = _node.Backend.LockedObject;
            var elapsed = ActivityFormat.Elapsed(_node.Backend.ElapsedSeconds);
            var what = (mode, obj) switch
            {
                (not null, not null) => $"waiting for {mode} on {obj}",
                (not null, null) => $"waiting for {mode}",
                (null, not null) => $"waiting on {obj}",
                _ => "waiting on a lock",
            };
            return elapsed.Length > 0 ? $"{what} · {elapsed}" : what;
        }
    }

    /// <summary>"blocking 3" — only when other backends wait behind this one.</summary>
    public string BlockingLabel =>
        _node.BlockedDescendants > 0 ? $"blocking {_node.BlockedDescendants}" : "";

    public string Query => ActivityFormat.OneLine(_node.Backend.Query);

    public IReadOnlyList<BlockingNode> Children { get; }
}

/// <summary>
/// Drives the server-activity window: periodic pg_stat_activity snapshots plus
/// cancel/terminate on the selected backend, and a companion who-blocks-whom
/// tree built from pg_blocking_pids. The 2s auto-refresh timer lives in the
/// window (a UI concern); this just exposes RefreshCommand for it to tick.
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    private readonly ActivityService _service;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPid))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    private ActivityRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPid))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    private BlockingNode? _selectedBlockingNode;

    /// <summary>
    /// Which tab is showing. The cancel/terminate buttons live once on the
    /// window's header line rather than once per tab, so they need to know
    /// whose selection they act on — see <see cref="TargetPid"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPid))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    [NotifyPropertyChangedFor(nameof(ActiveStatus))]
    private int _selectedTab;

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveStatus))]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveStatus))]
    private string _blockingStatus = "No lock waits.";

    [ObservableProperty]
    private bool _hasLockWaits;

    /// <summary>The backend the header actions act on: whatever the visible tab has selected.</summary>
    public int? TargetPid => SelectedTab == BlockingTab ? SelectedBlockingNode?.Pid : SelectedRow?.Pid;

    /// <summary>"alice@shop" for the confirm prompt; "" when nothing is selected.</summary>
    public string TargetLabel => SelectedTab == BlockingTab
        ? SelectedBlockingNode?.Identity ?? ""
        : SelectedRow is { } row ? $"{row.User}@{row.Database}" : "";

    public bool HasTarget => TargetPid is not null;

    /// <summary>The visible tab's status line — the window shows one status bar, not one per tab.</summary>
    public string ActiveStatus => SelectedTab == BlockingTab ? BlockingStatus : Status;

    private const int BlockingTab = 1;

    public ObservableCollection<ActivityRow> Rows { get; } = [];

    /// <summary>
    /// True once a poll has completed, successfully or not — the empty-state
    /// hint is gated on it so "no other backends" can't flash in the moment
    /// before the first snapshot lands. An unread grid and an empty one are
    /// different facts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBackends))]
    private bool _hasPolled;

    /// <summary>
    /// Drives the Backends tab's empty state. The view excludes our own
    /// connection, so a server nobody else is using legitimately has no rows —
    /// and a blank grid there reads as a failure rather than as an answer.
    /// </summary>
    public bool HasNoBackends => HasPolled && Rows.Count == 0;

    /// <summary>Roots of the blocking forest — the lock holders to cancel to unstick everyone below.</summary>
    public ObservableCollection<BlockingNode> BlockingRoots { get; } = [];

    // --- Trend ------------------------------------------------------------

    private readonly ActivityHistory _history;

    /// <summary>
    /// Busy backends per poll, oldest first — the sparkline's main series.
    /// Active rather than total, because a pool full of idle connections is the
    /// steady state everywhere and its line would never move. Replaced with a
    /// fresh array each refresh (see <see cref="ActivityHistory.Series"/>).
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<double?> _activeSeries = [];

    /// <summary>Backends waiting on a lock per poll, drawn over the active line — the shape you open this window to find.</summary>
    [ObservableProperty]
    private IReadOnlyList<double?> _lockWaitSeries = [];

    /// <summary>
    /// The peak across both series, which both sparklines are scaled to. Without
    /// a shared denominator each would auto-scale to its own maximum and two
    /// lock waiters would draw as tall as forty active backends — the overlay
    /// would be a lie.
    /// </summary>
    [ObservableProperty]
    private double _trendPeak = 1;

    /// <summary>
    /// True once there is more than one sample to draw. A one-point chart says
    /// nothing a number doesn't, so the trend stays hidden until the second poll
    /// rather than showing an empty frame (UI rule 1 — nothing always-visible
    /// that isn't earning its space).
    /// </summary>
    public bool HasTrend => _history.Count > 1;

    /// <summary>The window the sparkline covers, e.g. "last 2 min" — a chart with no time span is unreadable.</summary>
    public string TrendLabel
    {
        get
        {
            var samples = _history.Samples();
            if (samples.Count < 2)
            {
                return "";
            }

            var span = samples[^1].At - samples[0].At;
            return span.TotalMinutes >= 1
                ? $"last {span.TotalMinutes:F0} min"
                : $"last {span.TotalSeconds:F0}s";
        }
    }

    /// <summary>
    /// <paramref name="history"/> is the trend window this view fills as it
    /// polls; it is a parameter rather than a field initializer so a caller can
    /// hand in a pre-filled one (the screenshot harness does).
    /// </summary>
    public ActivityViewModel(ActivityService service, ActivityHistory? history = null)
    {
        _service = service;
        _history = history ?? new ActivityHistory();
        PublishTrend();
    }

    /// <summary>
    /// Folds this poll into the trend window. A failed poll records a gap rather
    /// than zeros — see <see cref="ActivitySample"/> — so a server that stopped
    /// answering doesn't draw like a server that went quiet.
    /// </summary>
    private void RecordTrend(int? backends, int? active, int? waiting)
    {
        _history.Record(new ActivitySample(DateTimeOffset.Now, backends, active, waiting));
        PublishTrend();
    }

    private void PublishTrend()
    {
        ActiveSeries = _history.Series(s => s.Active);
        LockWaitSeries = _history.Series(s => s.WaitingOnLock);
        TrendPeak = Math.Max(1, ActiveSeries.Concat(LockWaitSeries).Select(v => v ?? 0).DefaultIfEmpty(0).Max());
        OnPropertyChanged(nameof(HasTrend));
        OnPropertyChanged(nameof(TrendLabel));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Two independent queries (separate pooled connections) — overlap the
        // round-trips so a refresh tick isn't the sum of both latencies. Each
        // method owns its try/catch, so WhenAll never surfaces an exception; the
        // ObservableCollection mutations still run on the captured UI context.
        await Task.WhenAll(RefreshActivityAsync(), RefreshBlockingAsync());
    }

    private async Task RefreshActivityAsync()
    {
        try
        {
            var backends = await _service.GetActivityAsync(CancellationToken.None);

            // Swap the rows but keep the selection pinned to the same backend
            // (by pid), so an auto-refresh doesn't yank it away mid-decision.
            var selectedPid = SelectedRow?.Pid;
            Rows.Clear();
            foreach (var backend in backends)
            {
                Rows.Add(new ActivityRow(backend));
            }

            SelectedRow = selectedPid is { } pid ? Rows.FirstOrDefault(r => r.Pid == pid) : null;

            var locks = Rows.Count(r => r.IsWaitingOnLock);
            RecordTrend(Rows.Count, Rows.Count(r => r.State == "active"), locks);
            OnPropertyChanged(nameof(HasNoBackends));
            Status = $"{Rows.Count} backend{(Rows.Count == 1 ? "" : "s")}"
                + (locks > 0 ? $" · {locks} waiting on locks" : "")
                + $" · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            // A poll that failed is a hole in the trend, not a quiet server.
            RecordTrend(null, null, null);
            Status = ex.Message;
        }
        finally
        {
            HasPolled = true;
        }
    }

    private async Task RefreshBlockingAsync()
    {
        try
        {
            var backends = await _service.GetBlockingAsync(CancellationToken.None);
            var roots = BlockingTree.Build(backends);

            var selectedPid = SelectedBlockingNode?.Pid;
            BlockingRoots.Clear();
            foreach (var root in roots)
            {
                BlockingRoots.Add(new BlockingNode(root));
            }

            SelectedBlockingNode = selectedPid is { } pid ? FindByPid(BlockingRoots, pid) : null;
            HasLockWaits = BlockingRoots.Count > 0;

            var waiters = backends.Count(b => b.BlockedByPids.Count > 0);
            BlockingStatus = waiters == 0
                ? "No lock waits."
                : $"{waiters} backend{(waiters == 1 ? "" : "s")} waiting on {BlockingRoots.Count} lock holder{(BlockingRoots.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            BlockingStatus = ex.Message;
        }
    }

    private static BlockingNode? FindByPid(IEnumerable<BlockingNode> nodes, int pid)
    {
        foreach (var node in nodes)
        {
            if (node.Pid == pid)
            {
                return node;
            }

            if (FindByPid(node.Children, pid) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Stop the target's running statement but keep its session. On the Blocking
    /// tab the target is the selected node, so aiming at a lock holder releases
    /// everyone waiting beneath it.
    /// </summary>
    [RelayCommand]
    private Task CancelAsync() =>
        TargetPid is { } pid ? SignalCancelAsync(pid) : Task.CompletedTask;

    /// <summary>The view confirms first — this kills the whole session, not just the statement.</summary>
    [RelayCommand]
    private Task TerminateAsync() =>
        TargetPid is { } pid ? SignalTerminateAsync(pid) : Task.CompletedTask;

    private async Task SignalCancelAsync(int pid)
    {
        try
        {
            var signaled = await _service.CancelBackendAsync(pid, CancellationToken.None);
            var message = signaled ? $"Cancel signal sent to pid {pid}" : $"Pid {pid} is already gone";
            Status = message;
            BlockingStatus = message;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            BlockingStatus = ex.Message;
        }

        await RefreshAsync();
    }

    private async Task SignalTerminateAsync(int pid)
    {
        try
        {
            var signaled = await _service.TerminateBackendAsync(pid, CancellationToken.None);
            var message = signaled ? $"Terminated backend {pid}" : $"Pid {pid} is already gone";
            Status = message;
            BlockingStatus = message;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            BlockingStatus = ex.Message;
        }

        await RefreshAsync();
    }
}
