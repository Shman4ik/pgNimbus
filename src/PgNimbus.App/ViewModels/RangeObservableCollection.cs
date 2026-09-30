using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> with range operations that raise one
/// notification each. The same class kubeNimbus has, by the same name.
/// </summary>
/// <remarks>
/// <see cref="ObservableCollection{T}"/> has no range operations, so a list rebuilt with
/// <c>Clear</c> and an <c>Add</c> per item pays one notification per item, to the items
/// control and to everything else listening. The results grid rebuilds every column on
/// each change to <see cref="QueryViewModel.ColumnNames"/>, so filling it one <c>Add</c>
/// at a time built 1 + 2 + … + n columns for an n-column result (security audit 2026-09,
/// finding 16); the command palette re-added every relation of the database on each
/// keystroke; and a history Clear rebuilt the filtered list once per removed entry.
/// Avalonia's items controls accept multi-item Add and Remove notifications (its own
/// <c>AvaloniaList.AddRange</c> raises them), so <see cref="InsertRange"/> and
/// <see cref="RemoveRange"/> are one event each. <b>The DataGrid is the exception</b>:
/// bind it to a collection changed only through <see cref="ReplaceAll"/>, whose Reset
/// every consumer handles.
/// </remarks>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    // Collection<T>() backs itself with a List<T>, which is what makes the range methods available.
    private List<T> List => (List<T>)Items;

    /// <summary>Replaces every item, raising one Reset (nothing at all when it was and stays empty).</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var had = List.Count;
        List.Clear();
        List.AddRange(items);
        if (had == 0 && List.Count == 0)
        {
            return;
        }

        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Appends <paramref name="items"/> at the end with one Add notification.</summary>
    public void AddRange(IReadOnlyList<T> items) => InsertRange(List.Count, items);

    /// <summary>Inserts <paramref name="items"/> at <paramref name="index"/> with one Add notification.</summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        List.InsertRange(index, items);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, items as IList ?? items.ToList(), index));
    }

    /// <summary>Removes <paramref name="count"/> items from <paramref name="index"/> on with one Remove notification.</summary>
    public void RemoveRange(int index, int count)
    {
        count = Math.Min(count, List.Count - index);
        if (count <= 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = List.GetRange(index, count);
        List.RemoveRange(index, count);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, index));
    }

    private void RaiseCountChanged()
    {
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
    }
}
