using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Owns the saved-query list and run history, persisting both through their
/// respective stores. Save/load act on whichever tab <paramref name="getActiveQuery"/>
/// currently resolves to; history recording is driven by <see cref="RecordExecution"/>,
/// which callers wire up to each tab's <see cref="QueryViewModel.Executed"/> event, since
/// there can be more than one open tab at a time.
/// </summary>
public sealed partial class SavedQueriesViewModel : ObservableObject
{
    private readonly SavedQueryStore _savedQueryStore;
    private readonly QueryHistoryStore _historyStore;
    private readonly Func<QueryViewModel?> _getActiveQuery;
    private readonly Action<string?, string> _openInNewTab;
    private readonly Func<string?> _getConnectionLabel;

    [ObservableProperty]
    private string _newQueryName = string.Empty;

    /// <summary>Case-insensitive substring filter over the history entries' SQL.</summary>
    [ObservableProperty]
    private string _historyFilter = string.Empty;

    /// <summary>When on, only entries recorded against the current connection show.</summary>
    [ObservableProperty]
    private bool _scopeHistoryToConnection;

    public ObservableCollection<SavedQuery> SavedQueries { get; } = [];

    /// <summary>The full history, most recent first — the source of truth the filtered view derives from.</summary>
    public ObservableCollection<QueryHistoryEntry> History { get; } = [];

    /// <summary>What the history list actually shows: filter + scope applied, pinned entries floated to the top.</summary>
    public ObservableCollection<QueryHistoryEntry> FilteredHistory { get; } = [];

    /// <summary>Drives the empty-state hint under an empty saved-queries list.</summary>
    public bool HasNoSavedQueries => SavedQueries.Count == 0;

    /// <summary>Drives the empty-state hint under an empty history list.</summary>
    public bool HasNoHistory => History.Count == 0;

    /// <summary>True when history exists but the filter/scope hides all of it — drives a "no matches" hint.</summary>
    public bool HasNoHistoryMatches => History.Count > 0 && FilteredHistory.Count == 0;

    public SavedQueriesViewModel(SavedQueryStore savedQueryStore, QueryHistoryStore historyStore, Func<QueryViewModel?> getActiveQuery, Action<string?, string> openInNewTab, Func<string?>? getConnectionLabel = null)
    {
        _savedQueryStore = savedQueryStore;
        _historyStore = historyStore;
        _getActiveQuery = getActiveQuery;
        _openInNewTab = openInNewTab;
        _getConnectionLabel = getConnectionLabel ?? (() => null);

        foreach (var saved in _savedQueryStore.Load())
        {
            SavedQueries.Add(saved);
        }

        foreach (var entry in _historyStore.Load())
        {
            History.Add(entry);
        }

        SavedQueries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSavedQueries));
        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoHistory));
            ApplyHistoryFilter();
        };
        ApplyHistoryFilter();
    }

    /// <summary>
    /// Files an executed statement in the history — the single choke point for
    /// it, which is why the password redaction lives here rather than at a call
    /// site. Nothing in the Roles &amp; Permissions window routes a PASSWORD
    /// literal through a query tab (see <c>SecurityEditor</c>), but a user can
    /// always type <c>ALTER ROLE … PASSWORD 'x'</c> into the editor themselves,
    /// and <see cref="QueryHistoryStore"/> writes what it is given to disk in
    /// the clear.
    /// </summary>
    public void RecordExecution(QueryHistoryEntry entry)
    {
        entry = entry with { Sql = SecretRedactor.Redact(entry.Sql), Connection = _getConnectionLabel() };
        History.Insert(0, entry);
        _historyStore.Append(entry);
    }

    partial void OnHistoryFilterChanged(string value) => ApplyHistoryFilter();

    partial void OnScopeHistoryToConnectionChanged(bool value) => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        var scope = ScopeHistoryToConnection ? _getConnectionLabel() : null;
        var matches = History.Where(e =>
                (scope is null || e.Connection == scope) &&
                (HistoryFilter.Length == 0 || e.Sql.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Pinned)
            .ToList();

        FilteredHistory.Clear();
        foreach (var entry in matches)
        {
            FilteredHistory.Add(entry);
        }

        OnPropertyChanged(nameof(HasNoHistoryMatches));
    }

    [RelayCommand]
    private void ClearHistoryFilter() => HistoryFilter = string.Empty;

    /// <summary>Pins/unpins an entry: pinned ones float to the top and survive both the size cap and Clear.</summary>
    [RelayCommand]
    private void TogglePin(QueryHistoryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var index = History.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        History[index] = entry with { Pinned = !entry.Pinned };
        _historyStore.Save(History);
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(NewQueryName) && !string.IsNullOrWhiteSpace(_getActiveQuery()?.Sql);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveCurrentQuery()
    {
        if (_getActiveQuery() is not { } query)
        {
            return;
        }

        var saved = new SavedQuery(Guid.NewGuid(), NewQueryName.Trim(), query.Sql);
        SavedQueries.Add(saved);
        _savedQueryStore.Save(SavedQueries);
        NewQueryName = string.Empty;
    }

    [RelayCommand]
    private void DeleteSavedQuery(SavedQuery? query)
    {
        if (query is null)
        {
            return;
        }

        SavedQueries.Remove(query);
        _savedQueryStore.Save(SavedQueries);
    }

    // Loading always lands in a fresh tab: it must never overwrite whatever the
    // active tab holds (double-click on a list item takes the same path).
    [RelayCommand]
    private void LoadSavedQuery(SavedQuery? query)
    {
        if (query is not null)
        {
            _openInNewTab(query.Name, query.Sql);
        }
    }

    [RelayCommand]
    private void LoadHistoryEntry(QueryHistoryEntry? entry)
    {
        if (entry is not null)
        {
            _openInNewTab(null, entry.Sql);
        }
    }

    /// <summary>Clears the history except pinned entries — pinning is the "keep this" signal.</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            if (!History[i].Pinned)
            {
                History.RemoveAt(i);
            }
        }

        _historyStore.Save(History);
    }

    partial void OnNewQueryNameChanged(string value) => SaveCurrentQueryCommand.NotifyCanExecuteChanged();
}
