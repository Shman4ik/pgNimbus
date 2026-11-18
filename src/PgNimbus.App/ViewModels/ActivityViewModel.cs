using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Localization;
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
                : Strings.Monitoring_ActivitySeconds(time.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
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

    public string Client => Backend.ClientAddress ?? Strings.Monitoring_ActivityClientLocal;

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
public sealed class BlockingNode : ObservableObject
{
    private readonly BlockingTreeNode _node;

    public BlockingNode(BlockingTreeNode node)
    {
        _node = node;
        Children = node.Children.Select(c => new BlockingNode(c)).ToList();
        Localizer.WhenChanged(this, static n =>
        {
            n.OnPropertyChanged(nameof(WaitLabel));
            n.OnPropertyChanged(nameof(OtherBlockersLabel));
            n.OnPropertyChanged(nameof(BlockingLabel));
        });
    }

    /// <summary>
    /// Whether the node's row is open: every row starts open, so the wait chain
    /// shows at a glance. Kept here, not on the row, because the tree virtualizes
    /// and reuses a row scrolled out of view for another backend.
    /// </summary>
    public bool IsExpanded { get; set; } = true;

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
                (not null, not null) => Strings.Monitoring_ActivityWaitForModeOn(mode, obj),
                (not null, null) => Strings.Monitoring_ActivityWaitForMode(mode),
                (null, not null) => Strings.Monitoring_ActivityWaitOnObject(obj),
                _ => Strings.Monitoring_ActivityWaitOnLock,
            };
            return elapsed.Length > 0 ? Strings.Monitoring_ActivityWaitWithTime(what, elapsed) : what;
        }
    }

    /// <summary>
    /// The blockers this node doesn't sit under: "also blocked by 4822, 4823" for a
    /// waiter with several (the tree shows each backend once, under the first), or
    /// "blocked by 999" for a root whose blocker is outside the snapshot or in a
    /// deadlock cycle. Empty for the ordinary one-blocker waiter.
    /// </summary>
    public string OtherBlockersLabel
    {
        get
        {
            if (_node.AlsoBlockedBy.Count == 0)
            {
                return "";
            }

            var pids = string.Join(", ", _node.AlsoBlockedBy);
            return _node.Backend.BlockedByPids.Count > _node.AlsoBlockedBy.Count
                ? Strings.Monitoring_ActivityAlsoBlockedBy(pids)
                : Strings.Monitoring_ActivityBlockedBy(pids);
        }
    }

    public bool HasOtherBlockers => _node.AlsoBlockedBy.Count > 0;

    /// <summary>"blocking 3" — only when other backends wait behind this one.</summary>
    public string BlockingLabel =>
        _node.BlockedDescendants > 0 ? Strings.Monitoring_ActivityBlocking(_node.BlockedDescendants) : "";

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

    public ActivityViewModel(ActivityService service)
    {
        _service = service;
        Localizer.WhenChanged(this, static vm =>
        {
            vm.OnPropertyChanged(nameof(CancelTip));
            vm.OnPropertyChanged(nameof(TerminateTip));
        });
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPid))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    [NotifyPropertyChangedFor(nameof(CanSignal))]
    private ActivityRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPid))]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    [NotifyPropertyChangedFor(nameof(CanSignal))]
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
    [NotifyPropertyChangedFor(nameof(CanSignal))]
    [NotifyPropertyChangedFor(nameof(ActiveStatus))]
    private int _selectedTab;

    /// <summary>
    /// Why Cancel query and Terminate are off, or null when they are on. Set by
    /// the main window: a production window with writes off refuses them
    /// (ROADMAP T3). The server would allow both on a read-only session, so this
    /// one is pgNimbus's own check.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignal))]
    [NotifyPropertyChangedFor(nameof(CancelTip))]
    [NotifyPropertyChangedFor(nameof(TerminateTip))]
    private string? _signalsLockedHint;

    /// <summary>True when the header's Cancel query and Terminate act.</summary>
    public bool CanSignal => HasTarget && SignalsLockedHint is null;

    public string CancelTip => SignalsLockedHint ?? Strings.Monitoring_ActivityCancelTip;

    public string TerminateTip => SignalsLockedHint ?? Strings.Monitoring_ActivityTerminateTip;

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveStatus))]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveStatus))]
    private string _blockingStatus = Strings.Monitoring_ActivityNoLockWaitsStatus;

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

    public RangeObservableCollection<ActivityRow> Rows { get; } = [];

    /// <summary>Roots of the blocking forest — the lock holders to cancel to unstick everyone below.</summary>
    public RangeObservableCollection<BlockingNode> BlockingRoots { get; } = [];

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
            var backends = await Task.Run(() => _service.GetActivityAsync(CancellationToken.None));

            // Swap the rows but keep the selection pinned to the same backend
            // (by pid), so an auto-refresh doesn't yank it away mid-decision. One
            // Reset: an Add per backend cost the DataGrid a notification each,
            // every 2 s, on a server with hundreds of connections.
            var selectedPid = SelectedRow?.Pid;
            Rows.ReplaceAll(backends.Select(backend => new ActivityRow(backend)));

            SelectedRow = selectedPid is { } pid ? Rows.FirstOrDefault(r => r.Pid == pid) : null;

            var locks = Rows.Count(r => r.IsWaitingOnLock);
            Status = Strings.Monitoring_ActivityBackends(Rows.Count)
                + (locks > 0 ? " · " + Strings.Monitoring_ActivityWaitingOnLocks(locks) : "")
                + $" · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private async Task RefreshBlockingAsync()
    {
        try
        {
            var (backends, roots) = await Task.Run(async () =>
            {
                var read = await _service.GetBlockingAsync(CancellationToken.None);
                return (read, BlockingTree.Build(read));
            });

            var selectedPid = SelectedBlockingNode?.Pid;
            BlockingRoots.ReplaceAll(roots.Select(root => new BlockingNode(root)));

            SelectedBlockingNode = selectedPid is { } pid ? FindByPid(BlockingRoots, pid) : null;
            HasLockWaits = BlockingRoots.Count > 0;

            var waiters = backends.Count(b => b.BlockedByPids.Count > 0);
            BlockingStatus = waiters == 0
                ? Strings.Monitoring_ActivityNoLockWaitsStatus
                : Strings.Monitoring_ActivityBlockingStatus(
                    Strings.Monitoring_ActivityBackends(waiters),
                    Strings.Monitoring_ActivityLockHolders(BlockingRoots.Count));
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
        if (SignalsLockedHint is { } locked)
        {
            Status = locked;
            BlockingStatus = locked;
            return;
        }

        try
        {
            var signaled = await _service.CancelBackendAsync(pid, CancellationToken.None);
            var message = signaled ? Strings.Monitoring_ActivityCancelSent(pid) : Strings.Monitoring_ActivityPidGone(pid);
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
        if (SignalsLockedHint is { } locked)
        {
            Status = locked;
            BlockingStatus = locked;
            return;
        }

        try
        {
            var signaled = await _service.TerminateBackendAsync(pid, CancellationToken.None);
            var message = signaled ? Strings.Monitoring_ActivityTerminated(pid) : Strings.Monitoring_ActivityPidGone(pid);
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
