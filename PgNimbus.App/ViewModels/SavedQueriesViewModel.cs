using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Owns the saved-query list and run history, persisting both through their
/// respective stores. It is deliberately a store-with-a-list and not the thing
/// that decides <em>what</em> gets saved: <see cref="MainViewModel"/> owns the
/// active tab, so it resolves the SQL and the name and calls
/// <see cref="SaveQuery"/>. Loading always lands in a new tab, via the callback
/// the host supplies (UI design rule 3). History recording is driven by
/// <see cref="RecordExecution"/>, which callers wire up to each tab's
/// <see cref="QueryViewModel.Executed"/> event, since there can be more than
/// one open tab at a time.
/// </summary>
public sealed partial class SavedQueriesViewModel : ObservableObject
{
    private readonly SavedQueryStore _savedQueryStore;
    private readonly QueryHistoryStore _historyStore;
    private readonly Action<string?, string> _openInNewTab;
    private readonly Func<string?> _getConnectionLabel;

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

    public SavedQueriesViewModel(SavedQueryStore savedQueryStore, QueryHistoryStore historyStore, Action<string?, string> openInNewTab, Func<string?>? getConnectionLabel = null)
    {
        _savedQueryStore = savedQueryStore;
        _historyStore = historyStore;
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

    /// <summary>The entry with this id, or null if it has since been deleted.</summary>
    public SavedQuery? FindById(Guid id) => SavedQueries.FirstOrDefault(q => q.Id == id);

    /// <summary>
    /// The entry going by <paramref name="name"/>, or null. Case-insensitive,
    /// because "Daily report" and "daily report" being two rows in a list a
    /// person reads by eye is a bug, not a feature — the save dialog uses this
    /// to offer an overwrite instead of silently making the second one.
    /// </summary>
    public SavedQuery? FindByName(string name) =>
        SavedQueries.FirstOrDefault(q => string.Equals(q.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes <paramref name="sql"/> into the list under <paramref name="name"/>
    /// and returns the entry. When <paramref name="overwriteId"/> names a row
    /// that is still there, that row is replaced in place — same id, same
    /// position — so a tab that saves repeatedly updates one entry instead of
    /// growing a pile of duplicates. Anything else appends.
    /// </summary>
    public SavedQuery SaveQuery(string name, string sql, Guid? overwriteId = null)
    {
        var trimmed = name.Trim();
        var index = overwriteId is { } id ? IndexOfId(id) : -1;

        if (index >= 0)
        {
            var updated = SavedQueries[index] with { Name = trimmed, Sql = sql, UpdatedAt = DateTimeOffset.Now };
            SavedQueries[index] = updated;
            _savedQueryStore.Save(SavedQueries);
            return updated;
        }

        var saved = new SavedQuery(Guid.NewGuid(), trimmed, sql, DateTimeOffset.Now);
        SavedQueries.Add(saved);
        _savedQueryStore.Save(SavedQueries);
        return saved;
    }

    /// <summary>Renames an entry in place, leaving its SQL and id alone.</summary>
    public void RenameSavedQuery(SavedQuery query, string name)
    {
        var trimmed = name.Trim();
        var index = IndexOfId(query.Id);
        if (index < 0 || trimmed.Length == 0)
        {
            return;
        }

        SavedQueries[index] = SavedQueries[index] with { Name = trimmed, UpdatedAt = DateTimeOffset.Now };
        _savedQueryStore.Save(SavedQueries);
    }

    private int IndexOfId(Guid id)
    {
        for (var i = 0; i < SavedQueries.Count; i++)
        {
            if (SavedQueries[i].Id == id)
            {
                return i;
            }
        }

        return -1;
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
}
