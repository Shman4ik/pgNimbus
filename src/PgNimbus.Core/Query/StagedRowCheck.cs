using System.Collections;
using System.Text;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Query;

/// <summary>
/// A row as the user saw it when they first staged a change to it: the result
/// set's table columns and the values the grid held for them. Safe mode's
/// optimistic concurrency check compares the server's current row against this
/// at commit time.
/// </summary>
public sealed record RowSnapshot(IReadOnlyList<string> Columns, IReadOnlyList<object?> Values)
{
    public bool TryGetValue(string column, out object? value)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], column, StringComparison.Ordinal))
            {
                value = Values[i];
                return true;
            }
        }

        value = null;
        return false;
    }
}

/// <summary>How two reads of one cell relate.</summary>
public enum CellComparison
{
    Equal,
    Different,

    /// <summary>
    /// The two sides can't be compared honestly: one of them is a placeholder
    /// for a value the client couldn't read, or they arrived in different wire
    /// formats (one as a text literal, one as a typed value). Such a column is
    /// left out of the check rather than reported as a conflict nobody caused.
    /// </summary>
    Incomparable,
}

/// <summary>
/// Value equality for cells materialized by <see cref="QueryEngine"/>. Plain
/// <see cref="object.Equals(object, object)"/> is wrong for exactly the values
/// Postgres hands back as arrays (and <c>bytea</c>): two reads of one unchanged
/// row give two different array instances.
/// </summary>
public static class CellValueComparer
{
    public static CellComparison Compare(object? before, object? current)
    {
        if (QueryEngine.IsUnreadableCell(before) || QueryEngine.IsUnreadableCell(current))
        {
            return CellComparison.Incomparable;
        }

        if (before is null || current is null)
        {
            return before is null && current is null ? CellComparison.Equal : CellComparison.Different;
        }

        // A column read once through the text-format fallback and once as its
        // typed value (bit strings, hstore) — same value, different shape.
        if (before.GetType() != current.GetType() && (before is string || current is string))
        {
            return CellComparison.Incomparable;
        }

        return ValuesEqual(before, current) ? CellComparison.Equal : CellComparison.Different;
    }

    private static bool ValuesEqual(object before, object current)
    {
        if (before is Array a && current is Array b)
        {
            return ArraysEqual(a, b);
        }

        return Equals(before, current);
    }

    // Structural, any rank: the built-in IStructuralEquatable only handles
    // one-dimensional arrays, and Postgres happily returns int[][].
    private static bool ArraysEqual(Array a, Array b)
    {
        if (a.Rank != b.Rank)
        {
            return false;
        }

        for (var d = 0; d < a.Rank; d++)
        {
            if (a.GetLength(d) != b.GetLength(d))
            {
                return false;
            }
        }

        var left = a.GetEnumerator();
        var right = b.GetEnumerator();
        while (left.MoveNext() && right.MoveNext())
        {
            if (Compare(left.Current, right.Current) != CellComparison.Equal)
            {
                return false;
            }
        }

        return true;
    }

    // A hash consistent with Compare(...) == Equal for the non-array values a
    // key can hold. Arrays hash to a constant: they still match, just slower.
    internal static int KeyHash(IReadOnlyList<object?> values)
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value is null or Array ? 0 : value.GetHashCode());
        }

        return hash.ToHashCode();
    }
}

/// <summary>What the user staged against a row.</summary>
public enum StagedRowKind
{
    Edit,
    Delete,
}

/// <summary>Why a staged row can't be applied as staged.</summary>
public enum RowConflictKind
{
    /// <summary>Another session changed the row since it was loaded.</summary>
    Changed,

    /// <summary>The row is gone — another session deleted it, or changed its key.</summary>
    Deleted,
}

/// <summary>One column of a conflicting row: what the user saw, what the server holds now, and what they staged.</summary>
public sealed record ColumnComparison(
    string Column,
    object? Before,
    object? Current,
    bool HasProposed,
    object? Proposed,
    CellComparison BeforeVsCurrent)
{
    public bool ChangedElsewhere => BeforeVsCurrent == CellComparison.Different;
}

/// <summary>A staged row the server no longer agrees with.</summary>
public sealed record RowConflict(
    RowConflictKind Kind,
    StagedRowKind StagedAs,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<object?> KeyValues,
    IReadOnlyList<ColumnComparison> Columns,
    RowSnapshot? Current)
{
    /// <summary>The row's identity as the user would read it: <c>order_id = 7, line = 2</c>.</summary>
    public string KeyText => string.Join(", ", KeyColumns.Select((c, i) => $"{c} = {CellDisplay.Format(KeyValues[i], 60)}"));

    /// <summary>One or two sentences for the status bar and the conflict list.</summary>
    public string Describe()
    {
        var staged = StagedAs == StagedRowKind.Delete
            ? "you staged a delete"
            : "you staged " + string.Join(", ", Columns.Where(c => c.HasProposed).Select(c => $"{c.Column} = {CellDisplay.Format(c.Proposed, 40)}"));

        if (Kind == RowConflictKind.Deleted)
        {
            return $"Row {KeyText} no longer exists: another session deleted it or changed its key ({staged}).";
        }

        var changed = Columns.Where(c => c.ChangedElsewhere).ToList();
        var shown = string.Join("; ", changed.Take(3).Select(c =>
            $"{c.Column} was {CellDisplay.Format(c.Before, 40)}, now {CellDisplay.Format(c.Current, 40)}"));
        var more = changed.Count > 3 ? $"; +{changed.Count - 3} more column(s)" : "";
        return $"Row {KeyText} was changed by another session: {shown}{more} ({staged}).";
    }
}

/// <summary>
/// Renders a cell value for a conflict explanation: SQL-literal style, so
/// <c>NULL</c> and the string <c>'NULL'</c> stay distinguishable, and capped so
/// one jsonb document can't swallow the message.
/// </summary>
public static class CellDisplay
{
    public static string Format(object? value, int maxLength = 200)
    {
        var text = value switch
        {
            null => "NULL",
            byte[] bytes => SqlLiteral.Quote("\\x" + Convert.ToHexString(bytes.Length > 32 ? bytes[..32] : bytes).ToLowerInvariant()
                + (bytes.Length > 32 ? "…" : "")),
            Array array => SqlLiteral.Quote(PgValueSyntax.FormatArray(array)),
            BitArray bits => SqlLiteral.Quote(string.Concat(Enumerable.Range(0, bits.Length).Select(i => bits[i] ? '1' : '0'))),
            _ => SqlLiteral.Format(value),
        };

        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }
}

/// <summary>One staged row and what the check expects of it.</summary>
public sealed record StagedRowExpectation(
    StagedRowKind Kind,
    IReadOnlyList<object?> KeyValues,
    RowSnapshot? Original,
    IReadOnlyList<(string Column, object? Value)> Proposed);

/// <summary>
/// Safe mode's optimistic concurrency check, built by
/// <see cref="PendingChangeSet.BuildRowCheck"/> and run by
/// <see cref="QueryEngine.ApplyBatchAsync(IReadOnlyList{ParameterizedStatement}, StagedRowCheck?, CancellationToken)"/>
/// inside the commit's own transaction, before any staged statement:
/// <see cref="LockStatements"/> re-read every staged row <c>FOR UPDATE</c> (so
/// nobody can change it between the check and the write), and
/// <see cref="Evaluate"/> compares what came back with the row as the user saw
/// it. Any difference aborts the whole batch.
/// </summary>
public sealed class StagedRowCheck(
    IReadOnlyList<string> keyColumns,
    IReadOnlyList<string> selectedColumns,
    IReadOnlyList<StagedRowExpectation> rows,
    IReadOnlyList<ParameterizedStatement> lockStatements)
{
    /// <summary>
    /// How long the check waits for a row another session holds locked. Long
    /// enough to ride out a short transaction (whose committed result is then
    /// checked like any other change), short enough that a forgotten open
    /// transaction in someone's psql reads as "locked", not as a hung commit.
    /// </summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<string> KeyColumns { get; } = keyColumns;

    /// <summary>The key columns, then every column any snapshot carries — the order <see cref="LockStatements"/> select them in.</summary>
    public IReadOnlyList<string> SelectedColumns { get; } = selectedColumns;

    public IReadOnlyList<StagedRowExpectation> Rows { get; } = rows;

    public IReadOnlyList<ParameterizedStatement> LockStatements { get; } = lockStatements;

    /// <summary>
    /// Compares the rows the lock statements returned with the staged rows'
    /// snapshots. A staged row with no matching current row is
    /// <see cref="RowConflictKind.Deleted"/>; one whose snapshot differs in any
    /// comparable column is <see cref="RowConflictKind.Changed"/>, and carries
    /// every snapshot column (changed or not) so the explanation can show the
    /// whole row. Returns an empty list when the batch may proceed.
    /// </summary>
    public IReadOnlyList<RowConflict> Evaluate(IReadOnlyList<string> columnNames, IReadOnlyList<object?[]> currentRows)
    {
        var keyIndexes = KeyColumns.Select(k => IndexOf(columnNames, k)).ToArray();
        if (keyIndexes.Any(i => i < 0))
        {
            throw new InvalidOperationException("The row check's result doesn't carry the key columns.");
        }

        var byKey = new Dictionary<int, List<object?[]>>();
        foreach (var row in currentRows)
        {
            var hash = CellValueComparer.KeyHash(keyIndexes.Select(i => row[i]).ToArray());
            if (!byKey.TryGetValue(hash, out var bucket))
            {
                byKey[hash] = bucket = [];
            }

            bucket.Add(row);
        }

        var conflicts = new List<RowConflict>();
        foreach (var expected in Rows)
        {
            var current = byKey.TryGetValue(CellValueComparer.KeyHash(expected.KeyValues), out var candidates)
                ? candidates.FirstOrDefault(r => KeyMatches(r, keyIndexes, expected.KeyValues))
                : null;

            if (current is null)
            {
                conflicts.Add(new RowConflict(
                    RowConflictKind.Deleted,
                    expected.Kind,
                    KeyColumns,
                    expected.KeyValues,
                    Compare(expected, columnNames, current: null),
                    Current: null));
                continue;
            }

            if (expected.Original is not { } original)
            {
                // Staged without a snapshot: existence is all that can be checked.
                continue;
            }

            var comparisons = Compare(expected, columnNames, current);
            if (comparisons.Any(c => c.ChangedElsewhere))
            {
                var currentValues = original.Columns.Select(c => IndexOf(columnNames, c) is var i and >= 0 ? current[i] : null).ToArray();
                conflicts.Add(new RowConflict(
                    RowConflictKind.Changed,
                    expected.Kind,
                    KeyColumns,
                    expected.KeyValues,
                    comparisons,
                    new RowSnapshot(original.Columns, currentValues)));
            }
        }

        return conflicts;
    }

    private static List<ColumnComparison> Compare(StagedRowExpectation expected, IReadOnlyList<string> columnNames, object?[]? current)
    {
        var comparisons = new List<ColumnComparison>();
        var columns = expected.Original?.Columns ?? expected.Proposed.Select(p => p.Column).ToList();
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            var before = expected.Original is { } original ? original.Values[c] : null;
            var hasProposed = false;
            object? proposed = null;
            foreach (var (name, value) in expected.Proposed)
            {
                if (string.Equals(name, column, StringComparison.Ordinal))
                {
                    hasProposed = true;
                    proposed = value;
                }
            }

            var index = IndexOf(columnNames, column);
            var now = current is not null && index >= 0 ? current[index] : null;
            var comparison = current is null || index < 0 || expected.Original is null
                ? CellComparison.Incomparable
                : CellValueComparer.Compare(before, now);
            comparisons.Add(new ColumnComparison(column, before, now, hasProposed, proposed, comparison));
        }

        return comparisons;
    }

    private static bool KeyMatches(object?[] row, int[] keyIndexes, IReadOnlyList<object?> key)
    {
        for (var i = 0; i < keyIndexes.Length; i++)
        {
            if (CellValueComparer.Compare(row[keyIndexes[i]], key[i]) != CellComparison.Equal)
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// A safe-mode commit that was rolled back because the server no longer agrees
/// with what was staged. Nothing from the batch was applied. Either
/// <see cref="Conflicts"/> names the rows (changed or deleted elsewhere), or
/// <see cref="RowsLocked"/> says another session is holding one of them in an
/// open transaction, or the message explains a statement that didn't touch the
/// one row its key promised.
/// </summary>
public sealed class StagedChangesConflictException : Exception
{
    public StagedChangesConflictException(IReadOnlyList<RowConflict> conflicts)
        : base(Summarize(conflicts))
    {
        Conflicts = conflicts;
    }

    private StagedChangesConflictException(string message, bool rowsLocked, Exception? inner = null)
        : base(message, inner)
    {
        Conflicts = [];
        RowsLocked = rowsLocked;
    }

    public IReadOnlyList<RowConflict> Conflicts { get; }

    /// <summary>True when the check gave up waiting for a row lock another session holds.</summary>
    public bool RowsLocked { get; }

    public static StagedChangesConflictException Locked(Exception inner) => new(
        $"Commit rolled back — nothing was applied. A staged row is locked by another session's open transaction "
        + $"(waited {StagedRowCheck.LockTimeout.TotalSeconds:0} s). Try again once it commits or rolls back.",
        rowsLocked: true,
        inner);

    public static StagedChangesConflictException UnexpectedRowCount(int expected, int affected) => new(
        $"Commit rolled back — nothing was applied. A staged change was meant to touch {expected} row but touched {affected}; "
        + "the table's rows no longer match what was staged. Reload and stage the change again.",
        rowsLocked: false);

    private static string Summarize(IReadOnlyList<RowConflict> conflicts)
    {
        var changed = conflicts.Count(c => c.Kind == RowConflictKind.Changed);
        var deleted = conflicts.Count(c => c.Kind == RowConflictKind.Deleted);
        var parts = new List<string>(2);
        if (changed > 0)
        {
            parts.Add(changed == 1 ? "1 row was changed" : $"{changed} rows were changed");
        }

        if (deleted > 0)
        {
            parts.Add(deleted == 1 ? "1 row was deleted" : $"{deleted} rows were deleted");
        }

        var sb = new StringBuilder("Commit rolled back — nothing was applied. ")
            .Append(string.Join(" and ", parts))
            .Append(" by another session since you loaded them. ");
        if (conflicts.Count > 0)
        {
            sb.Append(conflicts[0].Describe());
        }

        return sb.ToString().TrimEnd();
    }
}
