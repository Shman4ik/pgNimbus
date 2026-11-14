using CommunityToolkit.Mvvm.ComponentModel;

namespace PgNimbus.App.ViewModels;

/// <summary>One row of the database list: a database of this server, and whether this window is on it.</summary>
public sealed record DatabaseSwitcherItem(string Name, bool IsCurrent);

/// <summary>
/// The list the title bar's database name opens (and the palette's "Switch
/// database…"): the server's databases this role may connect to, the current
/// one marked, filtered as the box above it is typed in. Built fresh each time
/// the list opens, so a database created or dropped since shows as it is now.
/// It neither connects nor knows how: choosing a row hands the name to the
/// host (<see cref="MainViewModel.SwitchToDatabaseAsync"/>), which connects
/// first and reports a failure on the window's status line.
/// </summary>
public sealed partial class DatabaseSwitcherViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _load;
    private readonly Func<string, Task> _switchTo;
    private readonly Func<string, Task>? _openInNewWindow;
    private IReadOnlyList<DatabaseSwitcherItem> _all = [];

    public DatabaseSwitcherViewModel(
        string currentDatabase,
        Func<CancellationToken, Task<IReadOnlyList<string>>> load,
        Func<string, Task> switchTo,
        Func<string, Task>? openInNewWindow)
    {
        CurrentDatabase = currentDatabase;
        _load = load;
        _switchTo = switchTo;
        _openInNewWindow = openInNewWindow;
    }

    public string CurrentDatabase { get; }

    /// <summary>The rows that match <see cref="Filter"/>, in the server's (name) order.</summary>
    public RangeObservableCollection<DatabaseSwitcherItem> Shown { get; } = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private DatabaseSwitcherItem? _selected;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Why the list is empty, or what went wrong reading it; null while rows are shown.</summary>
    [ObservableProperty]
    private string? _message;

    public bool HasMessage => Message is not null;

    /// <summary>Whether a row's right-click menu offers Open in New Window.</summary>
    public bool CanOpenInNewWindow => _openInNewWindow is not null;

    /// <summary>Raised when a choice was made and the list should close.</summary>
    public event Action? CloseRequested;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    /// <summary>Reads the list off the UI thread; a failure becomes <see cref="Message"/>.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Message = null;
        try
        {
            var names = await Task.Run(() => _load(ct), ct);
            _all = names.Select(n => new DatabaseSwitcherItem(n, n == CurrentDatabase)).ToList();
            IsLoading = false;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _all = [];
            Shown.ReplaceAll([]);
            Message = $"Couldn't list the databases: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        if (IsLoading)
        {
            return;
        }

        var query = Filter.Trim();
        var matches = query.Length == 0
            ? _all
            : _all.Where(d => d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Shown.ReplaceAll(matches);
        Selected = matches.FirstOrDefault(d => d.IsCurrent && query.Length == 0)
            ?? matches.FirstOrDefault();
        Message = _all.Count == 0 ? "No database this role may connect to."
            : matches.Count == 0 ? "No database matches."
            : null;
    }

    /// <summary>Moves the selection by <paramref name="delta"/> rows, clamped; the filter box's Up and Down.</summary>
    public void MoveSelection(int delta)
    {
        if (Shown.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Shown.IndexOf(Selected);
        Selected = Shown[Math.Clamp(index + delta, 0, Shown.Count - 1)];
    }

    /// <summary>Opens <paramref name="item"/> (default: the selected row) in this window. The current database is a no-op.</summary>
    public async Task ChooseAsync(DatabaseSwitcherItem? item = null)
    {
        item ??= Selected;
        if (item is null)
        {
            return;
        }

        CloseRequested?.Invoke();
        if (!item.IsCurrent)
        {
            await _switchTo(item.Name);
        }
    }

    /// <summary>Opens <paramref name="item"/> in a new window beside this one.</summary>
    public async Task OpenInNewWindowAsync(DatabaseSwitcherItem? item = null)
    {
        item ??= Selected;
        if (item is null || _openInNewWindow is null)
        {
            return;
        }

        CloseRequested?.Invoke();
        await _openInNewWindow(item.Name);
    }
}
