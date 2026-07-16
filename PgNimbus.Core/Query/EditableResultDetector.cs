using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Query;

/// <summary>
/// Decides whether an arbitrary result set maps cleanly back onto one table, so
/// a hand-typed SELECT can get the same inline editing browse mode has. Works
/// off the wire-protocol source metadata each <see cref="ColumnInfo"/> carries
/// (table OID + attribute number) rather than parsing SQL — an aliased column,
/// an expression, or a join instantly disqualifies via the metadata itself.
/// </summary>
public static class EditableResultDetector
{
    /// <summary>
    /// The single table every result column reads a distinct real attribute of,
    /// or null when any column is an expression/literal (no source table), the
    /// columns span more than one table, or the same attribute appears twice
    /// (a repeated column would make name-keyed cell commits ambiguous).
    /// </summary>
    public static uint? SingleSourceTableOid(IReadOnlyList<ColumnInfo> columns)
    {
        if (columns.Count == 0 || columns[0].TableOid == 0)
        {
            return null;
        }

        var oid = columns[0].TableOid;
        var seen = new HashSet<short>();
        foreach (var column in columns)
        {
            if (column.TableOid != oid || column.TableAttributeNumber <= 0 || !seen.Add(column.TableAttributeNumber))
            {
                return null;
            }
        }

        return oid;
    }

    /// <summary>
    /// The table's primary-key column names when the result can be edited
    /// safely, null otherwise. Safe means: every result column's displayed name
    /// is exactly the real name of the attribute it reads (no <c>AS</c> aliases —
    /// every commit path builds SET clauses and PK lookups from displayed
    /// names), and the table's full primary key is among the result columns
    /// (so each row can be targeted exactly). Callers must have already
    /// established via <see cref="SingleSourceTableOid"/> that all columns come
    /// from the table <paramref name="tableColumns"/> describes.
    /// </summary>
    public static IReadOnlyList<string>? MatchPrimaryKey(
        IReadOnlyList<ColumnInfo> resultColumns,
        IReadOnlyList<ColumnDetail> tableColumns)
    {
        var byAttNum = new Dictionary<short, ColumnDetail>(tableColumns.Count);
        foreach (var tableColumn in tableColumns)
        {
            byAttNum[tableColumn.AttNum] = tableColumn;
        }

        var presentAttNums = new HashSet<short>();
        foreach (var column in resultColumns)
        {
            if (!byAttNum.TryGetValue(column.TableAttributeNumber, out var tableColumn)
                || !string.Equals(tableColumn.Name, column.Name, StringComparison.Ordinal))
            {
                return null;
            }

            presentAttNums.Add(column.TableAttributeNumber);
        }

        var primaryKey = new List<string>();
        foreach (var tableColumn in tableColumns)
        {
            if (!tableColumn.IsPrimaryKey)
            {
                continue;
            }

            if (!presentAttNums.Contains(tableColumn.AttNum))
            {
                return null;
            }

            primaryKey.Add(tableColumn.Name);
        }

        return primaryKey.Count > 0 ? primaryKey : null;
    }
}
