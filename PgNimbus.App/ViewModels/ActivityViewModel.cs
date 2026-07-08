using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Monitoring;

namespace PgNimbus.App.ViewModels;

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

    public string Elapsed
    {
        get
        {
            if (Backend.State.Length == 0 || Backend.ElapsedSeconds <= 0)
            {
                return "";
            }

            var time = TimeSpan.FromSeconds(Backend.ElapsedSeconds);
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : time.TotalMinutes >= 1
                    ? $"{time.Minutes}:{time.Seconds:00}"
                    : $"{time.TotalSeconds:0.0}s";
        }
    }

    /// <summary>Single-line for the grid row — multi-line SQL would blow the row height out.</summary>
    public string Query => string.Join(' ', Backend.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Drives the server-activity window: periodic pg_stat_activity snapshots plus
/// cancel/terminate on the selected backend. The 2s auto-refresh timer lives in
/// the window (a UI concern); this just exposes RefreshCommand for it to tick.
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    private readonly ActivityService _service;

    [ObservableProperty]
    private ActivityRow? _selectedRow;

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    private string _status = "";

    public ObservableCollection<ActivityRow> Rows { get; } = [];

    public ActivityViewModel(ActivityService service)
    {
        _service = service;
    }

    [RelayCommand]
    private async Task RefreshAsync()
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

    [RelayCommand]
    private async Task CancelBackendAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            var signaled = await _service.CancelBackendAsync(row.Pid, CancellationToken.None);
            Status = signaled ? $"Cancel signal sent to pid {row.Pid}" : $"Pid {row.Pid} is already gone";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }

        await RefreshAsync();
    }

    /// <summary>The view confirms first — this kills the whole session, not just the statement.</summary>
    [RelayCommand]
    private async Task TerminateBackendAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            var signaled = await _service.TerminateBackendAsync(row.Pid, CancellationToken.None);
            Status = signaled ? $"Terminated backend {row.Pid}" : $"Pid {row.Pid} is already gone";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }

        await RefreshAsync();
    }
}
