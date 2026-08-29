using System.Collections;

namespace PgNimbus.App;

/// <summary>
/// Orders result rows (<c>object?[]</c>) by one cell for DataGrid
/// header-click sorting. The grid's columns bind to the whole row through a
/// converter (no reflection path), so the stock path-based sort has nothing
/// to work with - each column supplies one of these instead. NULLs sort
/// last; same-type <see cref="IComparable"/> values (ints, timestamps,
/// strings...) compare natively, anything else falls back to an ordinal
/// string comparison.
/// </summary>
public sealed class RowCellComparer(int index) : IComparer
{
    private readonly int _index = index;

    public int Compare(object? x, object? y)
    {
        var a = (x as object?[])?[_index];
        var b = (y as object?[])?[_index];

        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        if (a.GetType() == b.GetType() && a is IComparable comparable)
        {
            return comparable.CompareTo(b);
        }

        return string.CompareOrdinal(a.ToString(), b.ToString());
    }
}
