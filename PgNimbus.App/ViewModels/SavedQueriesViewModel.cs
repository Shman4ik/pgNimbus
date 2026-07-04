using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;

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

    [ObservableProperty]
    private string _newQueryName = string.Empty;

    public ObservableCollection<SavedQuery> SavedQueries { get; } = [];

    public ObservableCollection<QueryHistoryEntry> History { get; } = [];

    public SavedQueriesViewModel(SavedQueryStore savedQueryStore, QueryHistoryStore historyStore, Func<QueryViewModel?> getActiveQuery)
    {
        _savedQueryStore = savedQueryStore;
        _historyStore = historyStore;
        _getActiveQuery = getActiveQuery;

        foreach (var saved in _savedQueryStore.Load())
        {
            SavedQueries.Add(saved);
        }

        foreach (var entry in _historyStore.Load())
        {
            History.Add(entry);
        }
    }

    public void RecordExecution(QueryHistoryEntry entry)
    {
        History.Insert(0, entry);
        _historyStore.Append(entry);
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

    [RelayCommand]
    private void LoadSavedQuery(SavedQuery? query)
    {
        if (query is not null && _getActiveQuery() is { } active)
        {
            active.Sql = query.Sql;
        }
    }

    [RelayCommand]
    private void LoadHistoryEntry(QueryHistoryEntry? entry)
    {
        if (entry is not null && _getActiveQuery() is { } active)
        {
            active.Sql = entry.Sql;
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        _historyStore.Clear();
    }

    partial void OnNewQueryNameChanged(string value) => SaveCurrentQueryCommand.NotifyCanExecuteChanged();
}
