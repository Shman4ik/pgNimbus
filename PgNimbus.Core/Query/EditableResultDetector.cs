using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

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

    /// <summary>
    /// A primary-key column's type has no client-side reading (an unmapped
    /// composite, an extension type with no plugin), so its cells are
    /// placeholders: a row's identity can't be sent back to target it, and
    /// staging against it would check nothing.
    /// </summary>
    UnreadableKey,

    /// <summary>
    /// The query reads one relation more than once (a self-join, or a derived
    /// table or CTE over the same table), so two result columns can carry the
    /// same table OID while belonging to different rows. Also returned when the
    /// statement is nested too deeply to read, since then nobody can tell.
    /// </summary>
    RepeatedTable,
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
    /// True when <paramref name="columns"/> is non-empty and every column reads
    /// the table <paramref name="tableOid"/> (0 never matches). A browse tab
    /// resumes browse mode, and hands out an edit context for its table, only
    /// on this: a hand-edited page query that names the table without its
    /// schema can resolve along <c>search_path</c> to a different table with
    /// the same name, whose rows an edit context for the browsed one would
    /// then update by the wrong table's keys.
    /// </summary>
    public static bool ReadsOnlyTable(IReadOnlyList<ColumnInfo> columns, uint tableOid) =>
        tableOid != 0 && columns.Count > 0 && columns.All(c => c.TableOid == tableOid);

    /// <summary>
    /// Refuses a statement that reads one relation more than once, which the
    /// wire metadata alone cannot see: in <c>SELECT c.id, p.name FROM items c
    /// JOIN items p ON p.id = c.parent_id</c> both columns carry the OID of
    /// <c>items</c> with distinct attribute numbers, so <see cref="CheckSingleTable"/>
    /// accepts it, and an edit of <c>name</c> (the parent's) would update the
    /// child row. Counts the relations of every block whose columns can reach
    /// the result (the statement's branches, FROM subqueries, LATERAL items and
    /// CTE bodies), and each CTE reference as a name of its own; a subquery
    /// inside an expression (<c>EXISTS</c>, <c>IN</c>, a scalar subquery) is
    /// skipped, because its columns come back as expressions and never carry a
    /// table OID. Names are compared without their schema, so <c>a.items</c>
    /// and <c>b.items</c> count as one: refusing a rare edit is cheaper than
    /// guessing which is which. Anything the scope reader can't place (no
    /// query in the text, more than one statement) is left to the metadata
    /// checks and answers <see cref="EditBlocker.None"/>.
    /// </summary>
    public static EditBlocker CheckRepeatedTable(string sql)
    {
        var statements = SqlScriptSplitter.Split(sql);
        if (statements.Count != 1 || SqlScopeModel.Parse(statements[0]).Root is not { } root)
        {
            return EditBlocker.None;
        }

        return ReadsEachRelationOnce(root, new HashSet<string>(StringComparer.Ordinal))
            ? EditBlocker.None
            : EditBlocker.RepeatedTable;
    }

    private static bool ReadsEachRelationOnce(SqlQuery query, HashSet<string> seen)
    {
        if (query.IsOpaque)
        {
            return false;
        }

        foreach (var cte in query.Ctes)
        {
            if (!ReadsEachRelationOnce(cte.Body, seen))
            {
                return false;
            }
        }

        foreach (var block in query.Branches)
        {
            foreach (var source in block.Sources)
            {
                if (source.Derived is null && !source.IsFunction && source.Name.Length > 0 && !seen.Add(source.Name))
                {
                    return false;
                }
            }

            foreach (var nested in block.Nested)
            {
                if (nested.Role != SqlQueryRole.Expression && !ReadsEachRelationOnce(nested, seen))
                {
                    return false;
                }
            }
        }

        return true;
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

        if (FindUnreadableKey(resultColumns, pk) is not null)
        {
            return EditBlocker.UnreadableKey;
        }

        primaryKey = pk;
        return EditBlocker.None;
    }

    /// <summary>
    /// The first primary-key column whose values the client can't read (its CLR
    /// type resolved to <see cref="object"/> — see <c>QueryEngine.FieldType</c>),
    /// or null when every key part reads as a real value. A key column that
    /// came back through the text-format fallback reads as a string literal and
    /// is fine: the literal casts back to the key's type.
    /// </summary>
    public static ColumnInfo? FindUnreadableKey(IReadOnlyList<ColumnInfo> resultColumns, IReadOnlyList<string> primaryKey) =>
        resultColumns.FirstOrDefault(c => c.ClrType == typeof(object) && primaryKey.Contains(c.Name, StringComparer.Ordinal));
}
