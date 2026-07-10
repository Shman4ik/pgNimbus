using PgNimbus.Core.Query;

namespace PgNimbus.Core.Schema;

/// <summary>A table reference parsed out of a statement — schema-qualified name plus alias, if any.</summary>
public readonly record struct TableReference(string Schema, string Table, string? Alias);

/// <summary>
/// Pure FK-graph logic behind completion's JOIN magic: which tables are FK-adjacent
/// to the statement's tables, and what join condition connects two specific ones.
/// No UI/App dependency (unlike the SQL-text parsing that produces its inputs, which
/// stays in PgNimbus.App/Completion since it exists only to feed the editor popup) —
/// this half is pure data-in/data-out, so it's unit-testable without a live connection.
/// </summary>
public static class ForeignKeyMatcher
{
    /// <summary>
    /// Every table FK-adjacent to one already in <paramref name="statementTables"/>
    /// (either side of the relationship — the new table can be the "many" or the
    /// "one" side), excluding tables the statement already references. Order
    /// follows discovery (statement table order, then edge order); duplicates
    /// collapsed.
    /// </summary>
    public static IReadOnlyList<(string Schema, string Table)> FindJoinCandidates(
        IReadOnlyList<TableReference> statementTables, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in statementTables)
        {
            already.Add(table.Table);
            if (!string.IsNullOrEmpty(table.Schema))
            {
                already.Add($"{table.Schema}.{table.Table}");
            }
        }

        var results = new List<(string Schema, string Table)>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in statementTables)
        {
            foreach (var fk in EdgesFor(table, foreignKeys))
            {
                var neighbor = Matches(fk.FromSchema, fk.FromTable, table)
                    ? (fk.ToSchema, fk.ToTable)
                    : (fk.FromSchema, fk.FromTable);

                if (already.Contains(neighbor.Item2)
                    || already.Contains($"{neighbor.Item1}.{neighbor.Item2}")
                    || !added.Add($"{neighbor.Item1}.{neighbor.Item2}"))
                {
                    continue;
                }

                results.Add(neighbor);
            }
        }

        return results;
    }

    /// <summary>
    /// The join condition connecting the last table in <paramref name="statementTables"/>
    /// to the closest earlier one it has a direct FK to — <c>child.fk_col = parent.pk_col</c>,
    /// AND-joined for a composite key — or null when none of the earlier tables has one.
    /// </summary>
    public static string? BuildJoinCondition(
        IReadOnlyList<TableReference> statementTables, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        if (statementTables.Count < 2)
        {
            return null;
        }

        var right = statementTables[^1];
        for (var i = statementTables.Count - 2; i >= 0; i--)
        {
            var left = statementTables[i];
            if (FindEdge(left, right, foreignKeys) is not { } match)
            {
                continue;
            }

            var (fk, leftIsChild) = match;
            var leftRef = left.Alias ?? SqlIdentifier.QuoteIfNeeded(left.Table);
            var rightRef = right.Alias ?? SqlIdentifier.QuoteIfNeeded(right.Table);
            var (childRef, childCols, parentRef, parentCols) = leftIsChild
                ? (leftRef, fk.FromColumns, rightRef, fk.ToColumns)
                : (rightRef, fk.FromColumns, leftRef, fk.ToColumns);

            return string.Join(" AND ", childCols.Zip(parentCols, (c, p) =>
                $"{childRef}.{SqlIdentifier.QuoteIfNeeded(c)} = {parentRef}.{SqlIdentifier.QuoteIfNeeded(p)}"));
        }

        return null;
    }

    // Which side of the edge is the "child" (the FK-column-holding, FromTable
    // side) determines which columns land on which alias in the condition.
    private static (ForeignKeyInfo Fk, bool LeftIsChild)? FindEdge(
        TableReference left, TableReference right, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        foreach (var fk in EdgesFor(left, foreignKeys))
        {
            if (Matches(fk.FromSchema, fk.FromTable, left) && Matches(fk.ToSchema, fk.ToTable, right))
            {
                return (fk, true);
            }

            if (Matches(fk.FromSchema, fk.FromTable, right) && Matches(fk.ToSchema, fk.ToTable, left))
            {
                return (fk, false);
            }
        }

        return null;
    }

    private static IEnumerable<ForeignKeyInfo> EdgesFor(TableReference table, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        foreach (var fk in foreignKeys)
        {
            if (Matches(fk.FromSchema, fk.FromTable, table) || Matches(fk.ToSchema, fk.ToTable, table))
            {
                yield return fk;
            }
        }
    }

    private static bool Matches(string schema, string table, TableReference reference) =>
        string.Equals(table, reference.Table, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(reference.Schema) || string.Equals(schema, reference.Schema, StringComparison.OrdinalIgnoreCase));
}
