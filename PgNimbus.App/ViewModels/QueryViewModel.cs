using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

public sealed partial class QueryViewModel : ObservableObject
{
    private readonly QueryEngine _engine;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _sql = "SELECT 1;";

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private EditableTableContext? _editContext;

    public bool IsEditable => EditContext is { PrimaryKeyColumns.Count: > 0 };

    public ObservableCollection<string> ColumnNames { get; } = [];

    public ObservableCollection<object?[]> Rows { get; } = [];

    public QueryViewModel(QueryEngine engine)
    {
        _engine = engine;
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsRunning = true;
        Status = "Running...";
        ColumnNames.Clear();
        Rows.Clear();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _engine.ExecuteAsync(Sql, ct);

            switch (result)
            {
                case ResultSet resultSet:
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
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
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

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnSqlChanged(string value) => EditContext = null;

    partial void OnEditContextChanged(EditableTableContext? value) => OnPropertyChanged(nameof(IsEditable));

    /// <summary>
    /// Commits an inline grid edit as a targeted UPDATE, or reverts the cell
    /// (with a proper UI refresh) if the edit context is missing/invalid or
    /// the statement fails.
    /// </summary>
    public async Task CommitCellEditAsync(object?[] originalRow, object?[] editedRow, int columnIndex)
    {
        if (EditContext is not { } context)
        {
            RevertCell(originalRow, editedRow, columnIndex);
            return;
        }

        var columnName = ColumnNames[columnIndex];

        if (context.PrimaryKeyColumns.Contains(columnName))
        {
            RevertCell(originalRow, editedRow, columnIndex);
            Status = "Editing primary key columns isn't supported yet.";
            return;
        }

        var pkIndexes = context.PrimaryKeyColumns.Select(pk => ColumnNames.IndexOf(pk)).ToList();
        if (pkIndexes.Any(i => i < 0))
        {
            RevertCell(originalRow, editedRow, columnIndex);
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

        var parameters = new Dictionary<string, object?> { ["value"] = editedRow[columnIndex] };
        for (var n = 0; n < pkIndexes.Count; n++)
        {
            parameters[$"pk{n}"] = originalRow[pkIndexes[n]];
        }

        try
        {
            await _engine.ExecuteNonQueryAsync(sql, parameters, CancellationToken.None);
            Status = $"Saved {context.Schema}.{context.Table}.{columnName}";
        }
        catch (Exception ex)
        {
            RevertCell(originalRow, editedRow, columnIndex);
            Status = $"Update failed: {ex.Message}";
        }
    }

    private void RevertCell(object?[] originalRow, object?[] editedRow, int columnIndex)
    {
        var index = Rows.IndexOf(editedRow);
        if (index < 0)
        {
            return;
        }

        var reverted = (object?[])editedRow.Clone();
        reverted[columnIndex] = originalRow[columnIndex];
        Rows[index] = reverted;
    }
}
