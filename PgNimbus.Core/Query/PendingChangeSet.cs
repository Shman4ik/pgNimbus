using System.Text;

namespace PgNimbus.Core.Query;

/// <summary>
/// One column's value in a staged INSERT. <see cref="ValueText"/> is the raw
/// text the user typed — it executes as a parameter cast to the column's
/// declared type server-side (<c>CAST(@p AS numeric(10,2))</c>), so Postgres
/// does the parsing; null means an explicit SQL NULL. Columns the user left
/// blank aren't staged at all, so their defaults apply.
/// </summary>
public sealed record PendingInsertValue(string Column, string DataType, string? ValueText);

/// <summary>
/// Safe mode's staging area: cell edits, row deletes, and row inserts against
/// a single table, held locally instead of executing one by one. Edits and
/// deletes are keyed by the row's primary-key values (captured before any
/// staging, and stable because primary-key columns can't be edited), so
/// repeated edits to a cell coalesce. A delete supersedes the row's staged
/// edits without discarding them: while the delete is staged the edits are
/// excluded from <see cref="Count"/> and the built statements, and un-staging
/// the delete brings them back.
/// <see cref="BuildStatements"/> turns the set into parameterized statements
/// for one-transaction execution; <see cref="BuildScript"/> renders the same
/// changes as a human-readable SQL script for review before committing.
/// <para>
/// Optimistic concurrency: the first time a row is staged (edit or delete) the
/// caller hands over a <see cref="RowSnapshot"/> of the row as the user saw it.
/// <see cref="BuildRowCheck"/> turns those into the commit-time check that
/// re-reads each row under a lock and aborts the batch if another session
/// changed or deleted it; <see cref="Rebase"/> and <see cref="Unstage"/> are the
/// two ways out of a conflict. Nothing here promises an undo after a commit —
/// once the batch commits it is the server's.
/// </para>
/// </summary>
public sealed class PendingChangeSet
{
    public string Schema { get; }

    public string Table { get; }

    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    // Per key column, the declared type to cast a key parameter to, or null
    // when the CLR value binds as the right type on its own. An enum or
    // composite key part arrives from the grid as text, and `enum_col = text`
    // has no operator — so without the cast a composite key with an enum part
    // targets nothing.
    private readonly IReadOnlyList<string?> _keyCastTypes;

    // The row as the user saw it when first staged, keyed like the changes.
    private readonly Dictionary<RowKey, RowSnapshot> _originals = [];

    // Ordered lists, not dictionaries: the review script and the executed
    // batch must list changes in the order they were staged, and the set stays
    // human-sized (it's hand-staged), so linear lookups are fine.
    private readonly List<EditedRow> _edits = [];
    private readonly List<RowKey> _deletes = [];
    private readonly List<IReadOnlyList<PendingInsertValue>> _inserts = [];

    public PendingChangeSet(string schema, string table, IReadOnlyList<string> primaryKeyColumns, IReadOnlyList<string?>? keyCastTypes = null)
    {
        if (primaryKeyColumns.Count == 0)
        {
            throw new ArgumentException("Staged changes need primary-key columns to target rows.", nameof(primaryKeyColumns));
        }

        if (keyCastTypes is not null && keyCastTypes.Count != primaryKeyColumns.Count)
        {
            throw new ArgumentException("One cast type (or null) per primary-key column.", nameof(keyCastTypes));
        }

        Schema = schema;
        Table = table;
        PrimaryKeyColumns = primaryKeyColumns;
        _keyCastTypes = keyCastTypes ?? new string?[primaryKeyColumns.Count];
    }

    /// <summary>
    /// Total staged changes: edited rows (however many cells each, and not
    /// counting rows whose staged delete supersedes their edits) + deletes +
    /// inserts.
    /// </summary>
    public int Count => ActiveEdits.Count() + _deletes.Count + _inserts.Count;

    // Edited rows whose edits would actually execute — a staged delete on the
    // same row wins while it's staged.
    private IEnumerable<EditedRow> ActiveEdits => _edits.Where(e => !_deletes.Contains(e.Key));

    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Stages one cell's new value, replacing any earlier staged value for the
    /// same cell. Rejects primary-key columns (the key is what targets the
    /// UPDATE) and rows already staged for deletion.
    /// <paramref name="castType"/> is the column's declared type for values
    /// that must be parsed server-side (enum labels, array/composite literals
    /// staged as raw text) — the built UPDATE wraps the parameter in
    /// <c>CAST(@p AS type)</c>, matching how staged INSERT values execute.
    /// <paramref name="original"/> is the row as the user saw it; only the
    /// first snapshot of a row is kept, since later ones already carry staged
    /// values.
    /// </summary>
    public void StageEdit(object?[] pkValues, string column, object? value, string? castType = null, RowSnapshot? original = null)
    {
        var key = MakeKey(pkValues);

        if (PrimaryKeyColumns.Contains(column))
        {
            throw new ArgumentException($"Primary key column {column} can't be edited.", nameof(column));
        }

        if (_deletes.Contains(key))
        {
            throw new InvalidOperationException("This row is staged for deletion — press Delete on it again to unstage the delete first.");
        }

        var row = _edits.FirstOrDefault(e => e.Key.Equals(key));
        if (row is null)
        {
            _edits.Add(row = new EditedRow(key));
        }

        var index = row.Cells.FindIndex(c => c.Column == column);
        if (index >= 0)
        {
            row.Cells[index] = (column, value, castType);
        }
        else
        {
            row.Cells.Add((column, value, castType));
        }

        Remember(key, original);
    }

    /// <summary>
    /// Stages a row delete. Any staged edits for the row are kept but dormant
    /// (the DELETE supersedes them) until the delete is unstaged. A no-op if
    /// already staged.
    /// </summary>
    public void StageDelete(object?[] pkValues, RowSnapshot? original = null)
    {
        var key = MakeKey(pkValues);
        if (!_deletes.Contains(key))
        {
            _deletes.Add(key);
        }

        Remember(key, original);
    }

    /// <summary>Removes a staged delete. Returns false when the row wasn't staged.</summary>
    public bool UnstageDelete(object?[] pkValues)
    {
        var key = MakeKey(pkValues);
        if (!_deletes.Remove(key))
        {
            return false;
        }

        if (!_edits.Any(e => e.Key.Equals(key)))
        {
            _originals.Remove(key);
        }

        return true;
    }

    /// <summary>The snapshot a row was staged against, or null (not staged, or staged without one).</summary>
    public RowSnapshot? GetOriginal(object?[] pkValues) =>
        _originals.TryGetValue(MakeKey(pkValues), out var snapshot) ? snapshot : null;

    /// <summary>
    /// Columns the concurrency check can't compare, because the value the user
    /// saw was a placeholder for a type the client couldn't read. The review
    /// dialog lists them so "checked" never quietly means "partly checked".
    /// </summary>
    public IReadOnlyList<string> UncheckedColumns =>
        _originals.Values
            .SelectMany(s => s.Columns.Where((_, i) => QueryEngine.IsUnreadableCell(s.Values[i])))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Stages an INSERT. An empty value list means "all defaults" (<c>INSERT … DEFAULT VALUES</c>).</summary>
    public void StageInsert(IReadOnlyList<PendingInsertValue> values) => _inserts.Add(values);

    public bool IsRowDeleted(object?[] pkValues) => _deletes.Contains(MakeKey(pkValues));

    public bool IsRowEdited(object?[] pkValues)
    {
        var key = MakeKey(pkValues);
        return _edits.Any(e => e.Key.Equals(key));
    }

    /// <summary>The staged (column, value) pairs for a row, or null when none are staged — used to re-apply staged values after the grid reloads from the server.</summary>
    public IReadOnlyList<(string Column, object? Value)>? GetRowEdits(object?[] pkValues)
    {
        var key = MakeKey(pkValues);
        return _edits.FirstOrDefault(e => e.Key.Equals(key))?.Cells
            .Select(c => (c.Column, c.Value))
            .ToList();
    }

    public void Clear()
    {
        _edits.Clear();
        _deletes.Clear();
        _inserts.Clear();
        _originals.Clear();
    }

    /// <summary>
    /// "Reload and restage" after a conflict: rows another session changed keep
    /// their staged values but are re-based onto the server's current row, so
    /// the next commit checks against what the user has now been shown; rows
    /// that no longer exist are dropped, since there is nothing left to update
    /// or delete. Returns how many rows went each way.
    /// </summary>
    public (int Rebased, int Dropped) Rebase(IEnumerable<RowConflict> conflicts)
    {
        var rebased = 0;
        var dropped = 0;
        foreach (var conflict in conflicts)
        {
            var key = MakeKey(conflict.KeyValues.ToArray());
            if (conflict.Kind == RowConflictKind.Deleted || conflict.Current is null)
            {
                Forget(key);
                dropped++;
            }
            else
            {
                _originals[key] = conflict.Current;
                rebased++;
            }
        }

        return (rebased, dropped);
    }

    /// <summary>Drops every staged change to the conflicting rows, leaving the rest of the set staged. Returns how many rows were unstaged.</summary>
    public int Unstage(IEnumerable<RowConflict> conflicts)
    {
        var count = 0;
        foreach (var conflict in conflicts)
        {
            Forget(MakeKey(conflict.KeyValues.ToArray()));
            count++;
        }

        return count;
    }

    private void Forget(RowKey key)
    {
        _edits.RemoveAll(e => e.Key.Equals(key));
        _deletes.Remove(key);
        _originals.Remove(key);
    }

    private void Remember(RowKey key, RowSnapshot? original)
    {
        if (original is not null)
        {
            _originals.TryAdd(key, original);
        }
    }

    /// <summary>
    /// The commit-time concurrency check for every staged edit and delete, or
    /// null when nothing staged targets an existing row (inserts only). Rows
    /// are locked in key order, <paramref name="chunkSize"/> keys per
    /// statement, so a page-sized multi-row delete costs a handful of round
    /// trips rather than one per row.
    /// </summary>
    public StagedRowCheck? BuildRowCheck(int chunkSize = 500)
    {
        var rows = new List<StagedRowExpectation>();
        foreach (var row in ActiveEdits)
        {
            rows.Add(new StagedRowExpectation(
                StagedRowKind.Edit,
                row.Key.Values,
                _originals.GetValueOrDefault(row.Key),
                row.Cells.Select(c => (c.Column, c.Value)).ToList()));
        }

        foreach (var key in _deletes)
        {
            rows.Add(new StagedRowExpectation(StagedRowKind.Delete, key.Values, _originals.GetValueOrDefault(key), []));
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var columns = PrimaryKeyColumns.ToList();
        foreach (var row in rows)
        {
            foreach (var column in row.Original?.Columns ?? [])
            {
                if (!columns.Contains(column))
                {
                    columns.Add(column);
                }
            }
        }

        var select = string.Join(", ", columns.Select(SqlIdentifier.Quote));
        var keyList = string.Join(", ", PrimaryKeyColumns.Select(SqlIdentifier.Quote));
        var target = PrimaryKeyColumns.Count == 1 ? keyList : $"({keyList})";

        var statements = rows.Chunk(Math.Max(1, chunkSize)).Select(chunk =>
        {
            var parameters = new Dictionary<string, object?>();
            var tuples = chunk.Select((row, n) =>
            {
                var parts = new List<string>(PrimaryKeyColumns.Count);
                for (var i = 0; i < PrimaryKeyColumns.Count; i++)
                {
                    var name = $"k{n}_{i}";
                    parameters[name] = row.KeyValues[i];
                    parts.Add(KeyParameter(name, i));
                }

                return parts.Count == 1 ? parts[0] : $"({string.Join(", ", parts)})";
            }).ToList();

            return new ParameterizedStatement(
                $"SELECT {select} FROM {QualifiedTable} WHERE {target} IN ({string.Join(", ", tuples)}) ORDER BY {keyList} FOR UPDATE",
                parameters);
        }).ToList();

        return new StagedRowCheck(PrimaryKeyColumns, columns, rows, statements);
    }

    /// <summary>
    /// The staged changes as parameterized statements, one per edited row
    /// (all of a row's cells in one UPDATE), delete, and insert. Ordered
    /// UPDATEs → DELETEs → INSERTs so a "delete this row, insert its
    /// replacement" pair can reuse a key within the one transaction.
    /// </summary>
    public IReadOnlyList<ParameterizedStatement> BuildStatements()
    {
        var statements = new List<ParameterizedStatement>(Count);

        foreach (var row in ActiveEdits)
        {
            var parameters = new Dictionary<string, object?>();
            var sets = new List<string>(row.Cells.Count);
            for (var i = 0; i < row.Cells.Count; i++)
            {
                var expression = row.Cells[i].CastType is { } cast ? $"CAST(@v{i} AS {cast})" : $"@v{i}";
                sets.Add($"{SqlIdentifier.Quote(row.Cells[i].Column)} = {expression}");
                parameters[$"v{i}"] = row.Cells[i].Value;
            }

            statements.Add(new ParameterizedStatement(
                $"UPDATE {QualifiedTable} SET {string.Join(", ", sets)} WHERE {WherePkClause(row.Key, parameters)}",
                parameters,
                ExpectedRowsAffected: 1));
        }

        foreach (var key in _deletes)
        {
            var parameters = new Dictionary<string, object?>();
            statements.Add(new ParameterizedStatement(
                $"DELETE FROM {QualifiedTable} WHERE {WherePkClause(key, parameters)}",
                parameters,
                ExpectedRowsAffected: 1));
        }

        foreach (var insert in _inserts)
        {
            statements.Add(BuildInsertStatement(insert));
        }

        return statements;
    }

    /// <summary>
    /// The staged changes rendered as a readable SQL script (values inlined as
    /// literals) for pre-commit review. Display only — execution always goes
    /// through the parameterized <see cref="BuildStatements"/>.
    /// </summary>
    public string BuildScript()
    {
        var script = new StringBuilder();

        foreach (var row in ActiveEdits)
        {
            var sets = row.Cells.Select(c =>
            {
                var literal = c.CastType is { } cast ? $"CAST({SqlLiteral.Format(c.Value)} AS {cast})" : SqlLiteral.Format(c.Value);
                return $"{SqlIdentifier.Quote(c.Column)} = {literal}";
            });
            script.Append("UPDATE ").Append(QualifiedTable)
                  .Append(" SET ").Append(string.Join(", ", sets))
                  .Append(" WHERE ").Append(WherePkScript(row.Key)).AppendLine(";");
        }

        foreach (var key in _deletes)
        {
            script.Append("DELETE FROM ").Append(QualifiedTable)
                  .Append(" WHERE ").Append(WherePkScript(key)).AppendLine(";");
        }

        foreach (var insert in _inserts)
        {
            script.AppendLine(RenderInsertScript(insert));
        }

        return script.ToString();
    }

    private string QualifiedTable => $"{SqlIdentifier.Quote(Schema)}.{SqlIdentifier.Quote(Table)}";

    // "pk0 = @pk0 AND pk1 = @pk1", adding the key's values to the statement's
    // parameter dictionary as it goes.
    private string WherePkClause(RowKey key, Dictionary<string, object?> parameters)
    {
        var clauses = new List<string>(PrimaryKeyColumns.Count);
        for (var i = 0; i < PrimaryKeyColumns.Count; i++)
        {
            clauses.Add($"{SqlIdentifier.Quote(PrimaryKeyColumns[i])} = {KeyParameter($"pk{i}", i)}");
            parameters[$"pk{i}"] = key.Values[i];
        }

        return string.Join(" AND ", clauses);
    }

    private string WherePkScript(RowKey key) =>
        string.Join(" AND ", PrimaryKeyColumns.Select((pk, i) =>
        {
            var literal = SqlLiteral.Format(key.Values[i]);
            return $"{SqlIdentifier.Quote(pk)} = {(_keyCastTypes[i] is { } cast ? $"CAST({literal} AS {cast})" : literal)}";
        }));

    private string KeyParameter(string name, int keyIndex) =>
        _keyCastTypes[keyIndex] is { } cast ? $"CAST(@{name} AS {cast})" : $"@{name}";

    private ParameterizedStatement BuildInsertStatement(IReadOnlyList<PendingInsertValue> values)
    {
        if (values.Count == 0)
        {
            return new ParameterizedStatement($"INSERT INTO {QualifiedTable} DEFAULT VALUES", new Dictionary<string, object?>());
        }

        var parameters = new Dictionary<string, object?>();
        var columns = new List<string>(values.Count);
        var expressions = new List<string>(values.Count);
        foreach (var value in values)
        {
            columns.Add(SqlIdentifier.Quote(value.Column));
            if (value.ValueText is null)
            {
                expressions.Add("NULL");
            }
            else
            {
                var name = $"p{parameters.Count}";
                // Cast the text parameter to the column's declared type so
                // Postgres parses "42"/"2024-01-01"/… into the real type.
                expressions.Add($"CAST(@{name} AS {value.DataType})");
                parameters[name] = value.ValueText;
            }
        }

        return new ParameterizedStatement(
            $"INSERT INTO {QualifiedTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", expressions)})",
            parameters);
    }

    private string RenderInsertScript(IReadOnlyList<PendingInsertValue> values)
    {
        if (values.Count == 0)
        {
            return $"INSERT INTO {QualifiedTable} DEFAULT VALUES;";
        }

        var columns = string.Join(", ", values.Select(v => SqlIdentifier.Quote(v.Column)));
        var expressions = string.Join(", ", values.Select(v =>
            v.ValueText is null ? "NULL" : $"CAST({SqlLiteral.Quote(v.ValueText)} AS {v.DataType})"));
        return $"INSERT INTO {QualifiedTable} ({columns}) VALUES ({expressions});";
    }

    private RowKey MakeKey(object?[] pkValues)
    {
        if (pkValues.Length != PrimaryKeyColumns.Count)
        {
            throw new ArgumentException(
                $"Expected {PrimaryKeyColumns.Count} primary-key value(s), got {pkValues.Length}.", nameof(pkValues));
        }

        return new RowKey(pkValues);
    }

    private sealed class EditedRow(RowKey key)
    {
        public RowKey Key { get; } = key;

        public List<(string Column, object? Value, string? CastType)> Cells { get; } = [];
    }

    // Structural equality over the primary-key values, so a row keeps its
    // identity across grid reloads (fresh arrays, equal values).
    private readonly struct RowKey(object?[] values) : IEquatable<RowKey>
    {
        public object?[] Values { get; } = values;

        public bool Equals(RowKey other)
        {
            if (Values.Length != other.Values.Length)
            {
                return false;
            }

            for (var i = 0; i < Values.Length; i++)
            {
                if (!Equals(Values[i], other.Values[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is RowKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
