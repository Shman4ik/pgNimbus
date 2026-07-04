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
}
