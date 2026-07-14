using PgNimbus.Core.Export;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Schema;

/// <summary>
/// One grid-navigable foreign-key hop from the table on screen to a related
/// table: <paramref name="TargetColumns"/> on
/// <paramref name="TargetSchema"/>.<paramref name="TargetTable"/> are filtered
/// by the current row's values of <paramref name="SourceColumns"/>
/// (positionally paired, plural only for a composite key).
/// </summary>
public sealed record ForeignKeyHop(
    string TargetSchema, string TargetTable,
    IReadOnlyList<string> TargetColumns,
    IReadOnlyList<string> SourceColumns)
{
    public string QualifiedTarget => $"{TargetSchema}.{TargetTable}";
}

/// <summary>
/// Turns the catalog's FK edges into grid navigation: from a cell in a known
/// table, which row does its foreign key reference (follow the key to the
/// parent), and which tables hold rows referencing this one (walk the key
/// backwards to the children). Pure edge-list matching — the values and the
/// resulting browse filter are supplied/produced separately so this stays
/// UI-free and unit-testable.
/// </summary>
public static class ForeignKeyNavigator
{
    /// <summary>
    /// The hop to the row a cell references: the FK on
    /// <paramref name="schema"/>.<paramref name="table"/> whose child columns
    /// include <paramref name="column"/>, or null when the column is no FK.
    /// </summary>
    public static ForeignKeyHop? FindReferencedRow(
        string schema, string table, string column, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        foreach (var fk in foreignKeys)
        {
            if (NameEquals(fk.FromSchema, schema) && NameEquals(fk.FromTable, table)
                && fk.FromColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                return new ForeignKeyHop(fk.ToSchema, fk.ToTable, fk.ToColumns, fk.FromColumns);
            }
        }

        return null;
    }

    /// <summary>
    /// The reverse hops from a key cell: every FK in the catalog whose
    /// referenced columns on <paramref name="schema"/>.<paramref name="table"/>
    /// include <paramref name="column"/> — one hop per referencing table
    /// (a table may appear twice when two of its FKs point here).
    /// </summary>
    public static IReadOnlyList<ForeignKeyHop> FindReferencingTables(
        string schema, string table, string column, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        var hops = new List<ForeignKeyHop>();
        foreach (var fk in foreignKeys)
        {
            if (NameEquals(fk.ToSchema, schema) && NameEquals(fk.ToTable, table)
                && fk.ToColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                hops.Add(new ForeignKeyHop(fk.FromSchema, fk.FromTable, fk.FromColumns, fk.ToColumns));
            }
        }

        return hops;
    }

    /// <summary>
    /// The browse-bar WHERE predicate that lands the hop:
    /// <c>order_id = 42</c>, composite keys AND-joined. Values are rendered as
    /// SQL literals (quoted/escaped like the INSERT exporter); returns null
    /// when any value is NULL — a NULL key references nothing, and a NULL
    /// equality predicate would silently match nothing.
    /// </summary>
    public static string? BuildFilter(IReadOnlyList<string> targetColumns, IReadOnlyList<object?> sourceValues)
    {
        if (targetColumns.Count != sourceValues.Count || targetColumns.Count == 0)
        {
            return null;
        }

        var parts = new string[targetColumns.Count];
        for (var i = 0; i < targetColumns.Count; i++)
        {
            if (sourceValues[i] is null or DBNull)
            {
                return null;
            }

            parts[i] = $"{SqlIdentifier.QuoteIfNeeded(targetColumns[i])} = {ResultExporter.FormatSqlLiteral(sourceValues[i])}";
        }

        return string.Join(" AND ", parts);
    }

    // Catalog-vs-catalog comparison; ordinal-insensitive to match how
    // ForeignKeyMatcher treats identifier casing.
    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
