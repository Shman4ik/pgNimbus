using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Owns the saved-query list and run history, persisting both through their
/// respective stores. Subscribes to <see cref="QueryViewModel.Executed"/> so
/// history is recorded without QueryViewModel knowing about persistence.
/// </summary>
public sealed partial class SavedQueriesViewModel : ObservableObject
{
    private readonly SavedQueryStore _savedQueryStore;
    private readonly QueryHistoryStore _historyStore;
    private readonly QueryViewModel _query;

    [ObservableProperty]
    private string _newQueryName = string.Empty;

    public ObservableCollection<SavedQuery> SavedQueries { get; } = [];

    public ObservableCollection<QueryHistoryEntry> History { get; } = [];

    public SavedQueriesViewModel(SavedQueryStore savedQueryStore, QueryHistoryStore historyStore, QueryViewModel query)
    {
        _savedQueryStore = savedQueryStore;
        _historyStore = historyStore;
        _query = query;

        foreach (var saved in _savedQueryStore.Load())
        {
            SavedQueries.Add(saved);
        }

        foreach (var entry in _historyStore.Load())
        {
            History.Add(entry);
        }

        _query.Executed += OnQueryExecuted;
    }

    private void OnQueryExecuted(QueryHistoryEntry entry)
    {
        History.Insert(0, entry);
        _historyStore.Append(entry);
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(NewQueryName) && !string.IsNullOrWhiteSpace(_query.Sql);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveCurrentQuery()
    {
        var saved = new SavedQuery(Guid.NewGuid(), NewQueryName.Trim(), _query.Sql);
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
        if (query is not null)
        {
            _query.Sql = query.Sql;
        }
    }

    [RelayCommand]
    private void LoadHistoryEntry(QueryHistoryEntry? entry)
    {
        if (entry is not null)
        {
            _query.Sql = entry.Sql;
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
