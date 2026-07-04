using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Npgsql;
using PgNimbus.Core.Export;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

public sealed partial class QueryViewModel : ObservableObject
{
    private readonly QueryEngine _engine;
    private readonly ExplainService _explainService;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<ColumnInfo> _columns = [];

    [ObservableProperty]
    private string _sql = "SELECT 1;";

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private EditableTableContext? _editContext;

    [ObservableProperty]
    private string _tabTitle = "Query";

    [ObservableProperty]
    private ExplainNodeViewModel? _explainRoot;

    [ObservableProperty]
    private string? _explainSummary;

    [ObservableProperty]
    private bool _isShowingPlan;

    public bool IsEditable => EditContext is { PrimaryKeyColumns.Count: > 0 };

    /// <summary>Single-root wrapper so the plan tree's TreeView can bind an IEnumerable ItemsSource to one node.</summary>
    public IReadOnlyList<ExplainNodeViewModel> ExplainRoots => ExplainRoot is null ? [] : [ExplainRoot];

    partial void OnExplainRootChanged(ExplainNodeViewModel? value) => OnPropertyChanged(nameof(ExplainRoots));

    public ObservableCollection<string> ColumnNames { get; } = [];

    public ObservableCollection<object?[]> Rows { get; } = [];

    /// <summary>Raised once per <see cref="RunAsync"/> completion (success, command, error, or cancellation) so a history tracker can record it without RunAsync knowing about persistence.</summary>
    public event Action<QueryHistoryEntry>? Executed;

    public QueryViewModel(QueryEngine engine, ExplainService explainService)
    {
        _engine = engine;
        _explainService = explainService;
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var executedSql = Sql;

        IsRunning = true;
        Status = "Running...";
        IsShowingPlan = false;
        ColumnNames.Clear();
        Rows.Clear();
        _columns = [];

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _engine.ExecuteAsync(executedSql, ct);

            switch (result)
            {
                case ResultSet resultSet:
                    _columns = resultSet.Columns;
                    foreach (var column in resultSet.Columns)
                    {
                        ColumnNames.Add(column.Name);
                    }

                    var rowCount = 0;
                    var firstByteMs = -1L;

                    await foreach (var batch in resultSet.Batches.WithCancellation(ct))
                    {
                        if (firstByteMs < 0)
                        {
                            firstByteMs = stopwatch.ElapsedMilliseconds;
                        }

                        foreach (var row in batch.Rows)
                        {
                            Rows.Add(row);
                        }

                        rowCount += batch.Rows.Count;
                        Status = $"{rowCount} rows ({firstByteMs} ms to first byte, {resultSet.Elapsed.TotalMilliseconds:F0} ms elapsed)";
                    }

                    Status = $"{rowCount} rows in {stopwatch.Elapsed.TotalMilliseconds:F0} ms ({firstByteMs} ms to first byte)";
                    break;

                case CommandResult commandResult:
                    Status = $"{commandResult.CommandTag} — {commandResult.RowsAffected} row(s) affected in {commandResult.Elapsed.TotalMilliseconds:F0} ms";
                    break;

                case QueryError error:
                    Status = $"Error: {error.Message}";
                    break;
            }

            Executed?.Invoke(new QueryHistoryEntry(executedSql, DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalMilliseconds, Status));
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
            Executed?.Invoke(new QueryHistoryEntry(executedSql, DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalMilliseconds, Status));
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ExplainAsync() => RunExplainAsync(analyze: false);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ExplainAnalyzeAsync() => RunExplainAsync(analyze: true);

    [RelayCommand]
    private void ShowResults() => IsShowingPlan = false;

    private async Task RunExplainAsync(bool analyze)
    {
        IsRunning = true;
        Status = analyze ? "Running EXPLAIN ANALYZE..." : "Running EXPLAIN...";

        try
        {
            var result = await _explainService.ExplainAsync(Sql, analyze, CancellationToken.None);
            ExplainRoot = new ExplainNodeViewModel(result.Root, result.Root.TotalCost);
            ExplainSummary = result.ExecutionTimeMs is { } execMs
                ? $"Planning: {result.PlanningTimeMs:F3} ms   Execution: {execMs:F3} ms"
                : $"Planning: {result.PlanningTimeMs:F3} ms";
            IsShowingPlan = true;
            Status = "Plan ready";
        }
        catch (PostgresException ex)
        {
            Status = $"Explain failed: {ex.MessageText}";
        }
        catch (Exception ex)
        {
            Status = $"Explain failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ExplainCommand.NotifyCanExecuteChanged();
        ExplainAnalyzeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSqlChanged(string value)
    {
        EditContext = null;
        IsShowingPlan = false;
    }

    partial void OnEditContextChanged(EditableTableContext? value) => OnPropertyChanged(nameof(IsEditable));

    /// <summary>
    /// Commits an inline grid edit (the raw text typed into the cell editor)
    /// as a targeted UPDATE. The grid's own column bindings are one-way, so
    /// <paramref name="row"/> is never mutated by the UI itself - on success
    /// this applies the converted value and replaces the row (via
    /// ObservableCollection replace, since mutating an array element in
    /// place doesn't raise a UI change notification); on failure it simply
    /// does nothing, since the row was never touched.
    /// </summary>
    public async Task CommitCellEditAsync(object?[] row, int columnIndex, string newValueText)
    {
        if (EditContext is not { } context)
        {
            Status = "Editing isn't available for this result set.";
            return;
        }

        var columnName = ColumnNames[columnIndex];

        if (context.PrimaryKeyColumns.Contains(columnName))
        {
            Status = "Editing primary key columns isn't supported yet.";
            return;
        }

        var pkIndexes = context.PrimaryKeyColumns.Select(pk => ColumnNames.IndexOf(pk)).ToList();
        if (pkIndexes.Any(i => i < 0))
        {
            Status = "Cannot edit: primary key column isn't present in this result set.";
            return;
        }

        var whereClause = string.Join(
            " AND ",
            context.PrimaryKeyColumns.Select((pk, n) => $"{SqlIdentifier.Quote(pk)} = @pk{n}"));

        var sql = $"""
            UPDATE {SqlIdentifier.Quote(context.Schema)}.{SqlIdentifier.Quote(context.Table)}
            SET {SqlIdentifier.Quote(columnName)} = @value
            WHERE {whereClause}
            """;

        object? newValue;
        try
        {
            newValue = ConvertEditedValue(newValueText, columnIndex);
        }
        catch (Exception ex)
        {
            Status = $"Invalid value for {columnName}: {ex.Message}";
            return;
        }

        var parameters = new Dictionary<string, object?> { ["value"] = newValue };
        for (var n = 0; n < pkIndexes.Count; n++)
        {
            parameters[$"pk{n}"] = row[pkIndexes[n]];
        }

        try
        {
            await _engine.ExecuteNonQueryAsync(sql, parameters, CancellationToken.None);

            var rowIndex = Rows.IndexOf(row);
            if (rowIndex >= 0)
            {
                var updated = (object?[])row.Clone();
                updated[columnIndex] = newValue;
                Rows[rowIndex] = updated;
            }

            Status = $"Saved {context.Schema}.{context.Table}.{columnName}";
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Converts the cell editor's raw text back to the column's real CLR
    /// type, so the parameter Npgsql sends matches what the column expects
    /// (a bare string parameter against, say, a numeric column fails with a
    /// Postgres type-mismatch error, since text isn't assignment-castable to
    /// most non-text types).
    /// </summary>
    private object ConvertEditedValue(string text, int columnIndex)
    {
        var targetType = columnIndex < _columns.Count ? _columns[columnIndex].ClrType : typeof(string);
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(string))
        {
            return text;
        }

        if (underlying == typeof(Guid))
        {
            return Guid.Parse(text);
        }

        if (typeof(IConvertible).IsAssignableFrom(underlying))
        {
            return Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
        }

        return text;
    }

    public void ExportCsv(Stream stream)
    {
        using var writer = new StreamWriter(stream, leaveOpen: true);
        ResultExporter.WriteCsv(writer, ColumnNames, Rows);
    }

    public void ExportJson(Stream stream) => ResultExporter.WriteJson(stream, ColumnNames, Rows);
}
