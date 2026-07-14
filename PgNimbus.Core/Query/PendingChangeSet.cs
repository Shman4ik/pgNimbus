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
/// </summary>
public sealed class PendingChangeSet
{
    public string Schema { get; }

    public string Table { get; }

    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    // Ordered lists, not dictionaries: the review script and the executed
    // batch must list changes in the order they were staged, and the set stays
    // human-sized (it's hand-staged), so linear lookups are fine.
    private readonly List<EditedRow> _edits = [];
    private readonly List<RowKey> _deletes = [];
    private readonly List<IReadOnlyList<PendingInsertValue>> _inserts = [];

    public PendingChangeSet(string schema, string table, IReadOnlyList<string> primaryKeyColumns)
    {
        if (primaryKeyColumns.Count == 0)
        {
            throw new ArgumentException("Staged changes need primary-key columns to target rows.", nameof(primaryKeyColumns));
        }

        Schema = schema;
        Table = table;
        PrimaryKeyColumns = primaryKeyColumns;
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
    /// </summary>
    public void StageEdit(object?[] pkValues, string column, object? value)
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
            row.Cells[index] = (column, value);
        }
        else
        {
            row.Cells.Add((column, value));
        }
    }

    /// <summary>
    /// Stages a row delete. Any staged edits for the row are kept but dormant
    /// (the DELETE supersedes them) until the delete is unstaged. A no-op if
    /// already staged.
    /// </summary>
    public void StageDelete(object?[] pkValues)
    {
        var key = MakeKey(pkValues);
        if (!_deletes.Contains(key))
        {
            _deletes.Add(key);
        }
    }

    /// <summary>Removes a staged delete. Returns false when the row wasn't staged.</summary>
    public bool UnstageDelete(object?[] pkValues) => _deletes.Remove(MakeKey(pkValues));

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
        return _edits.FirstOrDefault(e => e.Key.Equals(key))?.Cells;
    }

    public void Clear()
    {
        _edits.Clear();
        _deletes.Clear();
        _inserts.Clear();
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
                sets.Add($"{SqlIdentifier.Quote(row.Cells[i].Column)} = @v{i}");
                parameters[$"v{i}"] = row.Cells[i].Value;
            }

            statements.Add(new ParameterizedStatement(
                $"UPDATE {QualifiedTable} SET {string.Join(", ", sets)} WHERE {WherePkClause(row.Key, parameters)}",
                parameters));
        }

        foreach (var key in _deletes)
        {
            var parameters = new Dictionary<string, object?>();
            statements.Add(new ParameterizedStatement(
                $"DELETE FROM {QualifiedTable} WHERE {WherePkClause(key, parameters)}",
                parameters));
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
            var sets = row.Cells.Select(c => $"{SqlIdentifier.Quote(c.Column)} = {SqlLiteral.Format(c.Value)}");
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
            clauses.Add($"{SqlIdentifier.Quote(PrimaryKeyColumns[i])} = @pk{i}");
            parameters[$"pk{i}"] = key.Values[i];
        }

        return string.Join(" AND ", clauses);
    }

    private string WherePkScript(RowKey key) =>
        string.Join(" AND ", PrimaryKeyColumns.Select((pk, i) => $"{SqlIdentifier.Quote(pk)} = {SqlLiteral.Format(key.Values[i])}"));

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

        public List<(string Column, object? Value)> Cells { get; } = [];
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
