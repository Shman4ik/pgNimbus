using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Query;

/// <summary>
/// Why a result set can't be edited inline. <see cref="None"/> means it can.
/// The other members drive the status bar's read-only hint, so each maps to
/// one specific, user-explainable disqualifier.
/// </summary>
public enum EditBlocker
{
    None,

    /// <summary>Some column isn't a plain table attribute: an expression, a literal, or a system column like ctid.</summary>
    ComputedColumns,

    /// <summary>Columns read from more than one table (a join).</summary>
    MultipleTables,

    /// <summary>The same table attribute appears more than once — name-keyed cell commits would be ambiguous.</summary>
    RepeatedColumn,

    /// <summary>The source relation isn't an ordinary/partitioned table (a view, matview, …). Assigned by the caller after the catalog lookup.</summary>
    NotAPlainTable,

    /// <summary>A column's displayed name isn't the real attribute name (an <c>AS</c> alias) — every commit path is keyed by displayed names.</summary>
    RenamedColumns,

    /// <summary>The table has no primary key, so no row can be targeted exactly.</summary>
    NoPrimaryKey,

    /// <summary>The table has a primary key, but the result doesn't include all of it.</summary>
    PrimaryKeyNotSelected,
}

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
    /// Checks that every result column reads a distinct real attribute of one
    /// table, returning that table's OID through <paramref name="tableOid"/>
    /// (0 unless the answer is <see cref="EditBlocker.None"/>).
    /// </summary>
    public static EditBlocker CheckSingleTable(IReadOnlyList<ColumnInfo> columns, out uint tableOid)
    {
        tableOid = 0;
        if (columns.Count == 0)
        {
            return EditBlocker.ComputedColumns;
        }

        var oid = 0u;
        var seen = new HashSet<short>();
        foreach (var column in columns)
        {
            if (column.TableOid == 0 || column.TableAttributeNumber <= 0)
            {
                return EditBlocker.ComputedColumns;
            }

            if (oid == 0)
            {
                oid = column.TableOid;
            }
            else if (column.TableOid != oid)
            {
                return EditBlocker.MultipleTables;
            }

            if (!seen.Add(column.TableAttributeNumber))
            {
                return EditBlocker.RepeatedColumn;
            }
        }

        tableOid = oid;
        return EditBlocker.None;
    }

    /// <summary>
    /// Checks a result set against its source table's real columns: every
    /// column's displayed name must be exactly the attribute name it reads (no
    /// <c>AS</c> aliases — every commit path builds SET clauses and PK lookups
    /// from displayed names), and the table's full primary key must be among
    /// the result columns (so each row can be targeted exactly). On
    /// <see cref="EditBlocker.None"/>, <paramref name="primaryKey"/> holds the
    /// PK column names; empty otherwise. Callers must have already established
    /// via <see cref="CheckSingleTable"/> that all columns come from the table
    /// <paramref name="tableColumns"/> describes.
    /// </summary>
    public static EditBlocker MatchPrimaryKey(
        IReadOnlyList<ColumnInfo> resultColumns,
        IReadOnlyList<ColumnDetail> tableColumns,
        out IReadOnlyList<string> primaryKey)
    {
        primaryKey = [];

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
                return EditBlocker.RenamedColumns;
            }

            presentAttNums.Add(column.TableAttributeNumber);
        }

        var pk = new List<string>();
        var pkMissing = false;
        foreach (var tableColumn in tableColumns)
        {
            if (!tableColumn.IsPrimaryKey)
            {
                continue;
            }

            pk.Add(tableColumn.Name);
            pkMissing |= !presentAttNums.Contains(tableColumn.AttNum);
        }

        if (pk.Count == 0)
        {
            return EditBlocker.NoPrimaryKey;
        }

        if (pkMissing)
        {
            return EditBlocker.PrimaryKeyNotSelected;
        }

        primaryKey = pk;
        return EditBlocker.None;
    }
}
