using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole contents with
/// one <see cref="NotifyCollectionChangedAction.Reset"/> instead of an event per item.
/// </summary>
/// <remarks>
/// Why it exists: the results grid rebuilds every column on each change to
/// <see cref="QueryViewModel.ColumnNames"/>, so filling it one <c>Add</c> at a time
/// built 1 + 2 + … + n columns for an n-column result: about a thousand for the
/// 47-column telemetry demo, and two billion for the 65,535 columns a hostile server
/// can claim (security audit 2026-09, finding 16).
/// </remarks>
public sealed class ResettableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces every item, raising one Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
