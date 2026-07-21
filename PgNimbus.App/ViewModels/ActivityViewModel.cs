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
    private ActivityRow? _selectedRow;

    [ObservableProperty]
    private BlockingNode? _selectedBlockingNode;

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _blockingStatus = "No lock waits.";

    [ObservableProperty]
    private bool _hasLockWaits;

    public ObservableCollection<ActivityRow> Rows { get; } = [];

    /// <summary>Roots of the blocking forest — the lock holders to cancel to unstick everyone below.</summary>
    public ObservableCollection<BlockingNode> BlockingRoots { get; } = [];

    public ActivityViewModel(ActivityService service)
    {
        _service = service;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshActivityAsync();
        await RefreshBlockingAsync();
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
            Status = $"{Rows.Count} backend{(Rows.Count == 1 ? "" : "s")}"
                + (locks > 0 ? $" · {locks} waiting on locks" : "")
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

    [RelayCommand]
    private Task CancelBackendAsync() =>
        SelectedRow is { } row ? SignalCancelAsync(row.Pid) : Task.CompletedTask;

    /// <summary>The view confirms first — this kills the whole session, not just the statement.</summary>
    [RelayCommand]
    private Task TerminateBackendAsync() =>
        SelectedRow is { } row ? SignalTerminateAsync(row.Pid) : Task.CompletedTask;

    /// <summary>Cancel the selected node's statement — aimed at the lock holder to release the wait.</summary>
    [RelayCommand]
    private Task CancelBlockerAsync() =>
        SelectedBlockingNode is { } node ? SignalCancelAsync(node.Pid) : Task.CompletedTask;

    /// <summary>The view confirms first — terminate the selected node's whole session.</summary>
    [RelayCommand]
    private Task TerminateBlockerAsync() =>
        SelectedBlockingNode is { } node ? SignalTerminateAsync(node.Pid) : Task.CompletedTask;

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
