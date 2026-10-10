namespace PgNimbus.Core.Backup;

/// <summary>Where a backup is.</summary>
public enum BackupStage
{
    /// <summary>Connected, reading the catalog: pg_dump's "reading …" lines.</summary>
    ReadingSchema,

    /// <summary>Saving rows, one table at a time.</summary>
    SavingData,

    /// <summary>A structure-only backup writing its definitions.</summary>
    SavingStructure,

    /// <summary>Every table is saved; indexes, constraints and permissions are being written.</summary>
    Finishing,
}

/// <summary>One look at a running backup, for the progress bar and the line under it.</summary>
/// <param name="Fraction">How far along, 0 to 1, or null when there is nothing to measure it by (a structure-only backup).</param>
public sealed record BackupProgress(
    BackupStage Stage,
    string? CurrentTable,
    int TablesDone,
    int TablesTotal,
    double? Fraction,
    long BytesWritten,
    TimeSpan Elapsed);

/// <summary>
/// Turns pg_dump's <c>--verbose</c> lines into progress. pg_dump announces each
/// table as it starts on its rows (<c>dumping contents of table "sales.orders"</c>),
/// so a table counts as done when the next one starts, and the bar moves by
/// each finished table's share of the data on disk (a huge table and a lookup
/// table don't count the same). The list of tables comes from the catalog
/// before the run; a table it doesn't know (created since) still counts as
/// one more table done.
/// </summary>
public sealed class PgDumpProgressTracker
{
    private const string DumpingPrefix = "dumping contents of table \"";

    // A table with no rows on disk still takes a moment; this keeps an
    // all-empty database from sitting at 0 % until the end.
    private const long MinimumWeight = 8192;

    private readonly Dictionary<string, long> _weights = new(StringComparer.Ordinal);
    private readonly bool _structureOnly;
    private readonly long _totalWeight;
    private long _doneWeight;
    private long _currentWeight;
    private bool _anyStarted;

    public PgDumpProgressTracker(IReadOnlyList<BackupTable> tables, bool structureOnly)
    {
        _structureOnly = structureOnly;
        foreach (var table in tables)
        {
            var weight = Math.Max(table.Bytes, MinimumWeight);
            _weights[table.DumpName] = weight;
            _totalWeight += weight;
        }

        TablesTotal = tables.Count;
        Stage = BackupStage.ReadingSchema;
    }

    public BackupStage Stage { get; private set; }

    /// <summary>The table whose rows are being saved now.</summary>
    public string? CurrentTable { get; private set; }

    public int TablesDone { get; private set; }

    public int TablesTotal { get; private set; }

    /// <summary>The finished share of the data, or null for a structure-only backup.</summary>
    public double? Fraction => _structureOnly
        ? null
        : _totalWeight == 0
            ? (Stage == BackupStage.Finishing ? 1 : 0)
            : Math.Clamp((double)_doneWeight / _totalWeight, 0, 1);

    /// <summary>Takes one line of pg_dump's standard error; true when the progress changed.</summary>
    public bool Observe(string line)
    {
        var at = line.IndexOf(DumpingPrefix, StringComparison.Ordinal);
        if (at >= 0 && line.EndsWith('"'))
        {
            var name = line[(at + DumpingPrefix.Length)..^1];
            FinishCurrent();
            CurrentTable = name;
            _currentWeight = _weights.GetValueOrDefault(name, 0);
            _anyStarted = true;
            Stage = BackupStage.SavingData;
            TablesTotal = Math.Max(TablesTotal, TablesDone + 1);
            return true;
        }

        // A custom archive says it is saving the definitions once; a plain
        // script announces each one it writes ("creating TABLE …").
        if (Stage == BackupStage.ReadingSchema && _structureOnly
            && (line.Contains("saving database definition", StringComparison.Ordinal)
                || line.Contains(": creating ", StringComparison.Ordinal)))
        {
            Stage = BackupStage.SavingStructure;
            return true;
        }

        // After the last table pg_dump writes what comes after the data
        // (indexes, constraints, permissions): plain format announces each
        // ("creating CONSTRAINT …"), and a custom archive writes its table of
        // contents. Either way the rows are all saved.
        if (Stage == BackupStage.SavingData && line.Contains(": creating ", StringComparison.Ordinal))
        {
            Finish();
            return true;
        }

        return false;
    }

    /// <summary>Marks every table done: the program exited cleanly.</summary>
    public void Finish()
    {
        if (_anyStarted)
        {
            FinishCurrent();
        }

        Stage = BackupStage.Finishing;
        CurrentTable = null;
        _doneWeight = _totalWeight;
    }

    private void FinishCurrent()
    {
        if (CurrentTable is null)
        {
            return;
        }

        _doneWeight += _currentWeight;
        _currentWeight = 0;
        TablesDone++;
        CurrentTable = null;
    }

    /// <summary>The current state as a <see cref="BackupProgress"/>.</summary>
    public BackupProgress Snapshot(long bytesWritten, TimeSpan elapsed) =>
        new(Stage, CurrentTable, TablesDone, TablesTotal, Fraction, bytesWritten, elapsed);
}
