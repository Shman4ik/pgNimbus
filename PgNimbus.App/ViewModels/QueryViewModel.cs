using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Npgsql;
using PgNimbus.Core.Export;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

public sealed partial class QueryViewModel : ObservableObject
{
    /// <summary>
    /// Ceiling on rows kept in memory per result set. Streaming keeps the UI
    /// responsive during a huge SELECT, but every fetched row still lives on
    /// the client - without a cap an unbounded scan of a big table (or a
    /// runaway join) eventually exhausts memory. Past the cap the query is
    /// cancelled server-side via the same path as the Cancel button.
    /// </summary>
    public const int MaxDisplayRows = 100_000;

    private readonly QueryEngine _engine;
    private readonly ExplainService _explainService;

    // Supplies a catalog-backed reconciler on demand so a failed unquoted query
    // can offer a "did you mean the quoted form?" fix. Null in contexts that
    // don't wire it up (the fix affordance simply never appears).
    private readonly Func<CancellationToken, Task<IdentifierReconciler?>>? _reconcilerFactory;

    private CancellationTokenSource? _cts;
    private IReadOnlyList<ColumnInfo> _columns = [];

    // Live "still running" clock: a UI-thread timer ticks the elapsed time while a
    // query (or EXPLAIN) is in flight, so a slow statement that hasn't produced a
    // batch yet still shows visible progress instead of a frozen "Running…".
    private readonly Stopwatch _runClock = new();
    private DispatcherTimer? _runClockTimer;

    [ObservableProperty]
    private string _sql = "SELECT 1;";

    [ObservableProperty]
    private string _status = "Ready";

    /// <summary>Paints the status-bar message red for error outcomes.</summary>
    [ObservableProperty]
    private bool _hasError;

    // Structured status-bar segments (Files-style): each renders as its own
    // divided segment in the bottom bar; null collapses the segment.
    [ObservableProperty]
    private string? _rowCountText;

    [ObservableProperty]
    private string? _timingText;

    [ObservableProperty]
    private string? _capText;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Live elapsed time of the in-flight query ("1.2 s"), ticked while <see cref="IsRunning"/>; null when idle. Drives the status-bar running indicator alongside the indeterminate progress bar.</summary>
    [ObservableProperty]
    private string? _runningElapsedText;

    [ObservableProperty]
    private EditableTableContext? _editContext;

    /// <summary>Non-null when this tab is in no-SQL "browse a table" mode (filter/sort/paging bar shown).</summary>
    [ObservableProperty]
    private TableBrowseViewModel? _browse;

    // Primary-key columns of the browsed table, re-applied as the edit context
    // after each page load (composing the page SQL clears it via OnSqlChanged).
    private IReadOnlyList<string> _browsePkColumns = [];

    // Set while browse mode composes the page SQL, so OnSqlChanged doesn't treat
    // that programmatic write as a manual edit and tear browse mode down.
    private bool _applyingBrowseSql;

    /// <summary>Drives visibility of the browse (filter/sort/paging) bar.</summary>
    public bool IsBrowsing => Browse is not null;

    [ObservableProperty]
    private string _tabTitle = "Query";

    /// <summary>Fallback tab label ("Query N") used when the SQL names no table to derive a title from.</summary>
    [ObservableProperty]
    private string _defaultTitle = "Query";

    /// <summary>
    /// A fixed tab label that wins over both the SQL-derived name and
    /// <see cref="DefaultTitle"/> — set for special tabs (e.g. an object's
    /// reconstructed source) whose SQL wouldn't name them sensibly.
    /// </summary>
    [ObservableProperty]
    private string? _titleOverride;

    /// <summary>True when the SQL has been edited since it was last run — surfaced as a dot on the tab.</summary>
    [ObservableProperty]
    private bool _isDirty;

    // The SQL as of the last run; edits away from it mark the tab dirty.
    private string _lastRunSql;

    [ObservableProperty]
    private ExplainNodeViewModel? _explainRoot;

    [ObservableProperty]
    private string? _explainSummary;

    [ObservableProperty]
    private bool _isShowingPlan;

    public bool IsEditable => EditContext is { PrimaryKeyColumns.Count: > 0 };

    /// <summary>
    /// True when the results grid has nothing to show and isn't mid-run or displaying a plan — drives the
    /// empty-state hint ("Run a query"). Recomputed from the <see cref="Rows"/>, <see cref="IsShowingPlan"/>,
    /// and <see cref="IsRunning"/> change hooks.
    /// </summary>
    public bool HasNoResults => Rows.Count == 0 && !IsShowingPlan && !IsRunning && ResultSections.Count == 0;

    /// <summary>Single-root wrapper so the plan tree's TreeView can bind an IEnumerable ItemsSource to one node.</summary>
    public IReadOnlyList<ExplainNodeViewModel> ExplainRoots => ExplainRoot is null ? [] : [ExplainRoot];

    partial void OnExplainRootChanged(ExplainNodeViewModel? value) => OnPropertyChanged(nameof(ExplainRoots));

    /// <summary>
    /// One entry per statement when the editor holds a multi-statement script;
    /// empty for a single statement. Selecting an entry re-points the shared grid
    /// and status bar at that statement's result (see <see cref="OnSelectedSectionChanged"/>).
    /// </summary>
    public ObservableCollection<ScriptResultViewModel> ResultSections { get; } = [];

    [ObservableProperty]
    private ScriptResultViewModel? _selectedSection;

    /// <summary>True once a run produced more than one statement result — drives the section strip.</summary>
    public bool IsScriptResult => ResultSections.Count > 1;

    public ObservableCollection<string> ColumnNames { get; } = [];

    /// <summary>
    /// The grid's rows. Replaced wholesale (a fresh list instance) rather than
    /// mutated in bulk: the DataGrid's CollectionChanged handling costs
    /// ~200 µs per row whether the change arrives as per-item Adds, a range
    /// Add, or a Reset (~20 s for a capped 100k result, measured), while
    /// assigning a pre-populated ItemsSource costs ~10 ms because
    /// virtualization only ever realizes a viewport. The view watches this
    /// property and re-points DataGrid.ItemsSource at the new instance.
    /// Single-item mutations (inline cell edits) still notify normally and
    /// stay cheap.
    /// </summary>
    [ObservableProperty]
    private AvaloniaList<object?[]> _rows = [];

    /// <summary>
    /// The rewritten SQL a one-click fix would apply after a query fails on an
    /// unquoted identifier that resolves to a case-differing catalog name; null
    /// when there's no safe fix to offer. Drives the status-bar "Fix" affordance.
    /// </summary>
    [ObservableProperty]
    private string? _fixSuggestionSql;

    /// <summary>Human-readable summary of the pending fix (e.g. <c>Did you mean "games"."Spells"?</c>).</summary>
    [ObservableProperty]
    private string? _fixSuggestionText;

    /// <summary>Raised once per <see cref="RunAsync"/> completion (success, command, error, or cancellation) so a history tracker can record it without RunAsync knowing about persistence.</summary>
    public event Action<QueryHistoryEntry>? Executed;

    public QueryViewModel(
        QueryEngine engine,
        ExplainService explainService,
        Func<CancellationToken, Task<IdentifierReconciler?>>? reconcilerFactory = null)
    {
        _engine = engine;
        _explainService = explainService;
        _reconcilerFactory = reconcilerFactory;
        _lastRunSql = Sql;
        UpdateTabTitle();
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => RunCoreAsync(Sql, isEntireScript: true);

    /// <summary>
    /// Runs a single statement in isolation - e.g. the one the caret sits in,
    /// for <c>Shift</c>+<c>Enter</c> "smart execution" - without touching
    /// <see cref="Sql"/>, the dirty flag, or the multi-statement script/section
    /// machinery. The tab's on-screen script is left exactly as it was; only
    /// the grid and status bar reflect this one statement's outcome. A no-op
    /// while a run is already in flight, same as <see cref="RunCommand"/>.
    /// </summary>
    public Task RunStatementAsync(string statementSql) =>
        CanRun() ? RunCoreAsync(statementSql, isEntireScript: false) : Task.CompletedTask;

    private async Task RunCoreAsync(string executedSql, bool isEntireScript)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        if (isEntireScript)
        {
            // Running clears the dirty flag: the on-screen SQL is now what produced the result.
            _lastRunSql = executedSql;
            IsDirty = false;
        }

        IsRunning = true;
        Status = "Running...";
        HasError = false;
        FixSuggestionSql = null;
        FixSuggestionText = null;
        RowCountText = null;
        TimingText = null;
        CapText = null;
        IsShowingPlan = false;
        ColumnNames.Clear();
        Rows = [];
        _columns = [];
        SelectedSection = null;
        ResultSections.Clear();
        NotifyScriptResultChanged();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (isEntireScript)
            {
                // Split off the multi-statement script path. A single statement keeps the
                // streaming/editing/browse path below untouched; only a genuine script
                // (two or more statements) runs on one shared connection with per-statement
                // result sections. A statement run in isolation (RunStatementAsync) is
                // already exactly one statement, so it always takes the path below.
                var statements = SqlScriptSplitter.Split(executedSql);
                if (statements.Count > 1)
                {
                    await RunScriptAsync(statements, stopwatch, ct);
                    if (HasError)
                    {
                        await TryOfferFixAsync(executedSql);
                    }

                    Executed?.Invoke(new QueryHistoryEntry(executedSql, DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalMilliseconds, StatusSummary()));
                    return;
                }
            }

            // Ask for one row past the cap: receiving it proves the result was
            // actually cut short, so an exactly-at-the-cap result isn't
            // mislabeled as truncated.
            var result = await _engine.ExecuteAsync(executedSql, ct, MaxDisplayRows + 1);

            switch (result)
            {
                case ResultSet resultSet:
                    _columns = resultSet.Columns;
                    foreach (var column in resultSet.Columns)
                    {
                        ColumnNames.Add(column.Name);
                    }

                    var allRows = new List<object?[]>();
                    var firstByteMs = -1L;
                    var truncated = false;

                    // Read and materialize batches on a background thread. NpgsqlDataReader.ReadAsync
                    // frequently completes synchronously once data is already buffered, so consuming
                    // the async enumerable directly on the UI thread lets `await foreach` run many
                    // batches back-to-back without ever yielding — a big unbounded result set freezes
                    // the UI and makes Cancel unresponsive until the whole query finishes.
                    //
                    // The grid gets exactly two row deliveries: the first batch immediately (the
                    // "first screenful renders before the query finishes" promise), and the full
                    // list swapped in at the end (see the Rows doc comment for why bulk mutation
                    // of a bound collection is ruinously slow). In between, only the status line
                    // ticks — appended rows would land below the fold anyway.
                    try
                    {
                        await Task.Run(async () =>
                        {
                            var firstScreenShown = false;
                            var lastStatusMs = 0L;

                            await foreach (var batch in resultSet.Batches.WithCancellation(ct))
                            {
                                if (firstByteMs < 0)
                                {
                                    firstByteMs = stopwatch.ElapsedMilliseconds;
                                }

                                // The engine streams at most MaxDisplayRows + 1 rows;
                                // the sentinel row past the cap is dropped, not shown.
                                var rows = batch.Rows;
                                if (allRows.Count + rows.Count > MaxDisplayRows)
                                {
                                    rows = rows.Take(MaxDisplayRows - allRows.Count).ToList();
                                    truncated = true;
                                }

                                allRows.AddRange(rows);
                                var rowText = RowLabel(allRows.Count);
                                var timeText = $"{resultSet.Elapsed.TotalMilliseconds:F0} ms · first byte {firstByteMs} ms";

                                if (!firstScreenShown)
                                {
                                    firstScreenShown = true;
                                    var firstScreen = new AvaloniaList<object?[]>(allRows);
                                    await Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        Rows = firstScreen;
                                        RowCountText = rowText;
                                        TimingText = timeText;
                                    });
                                }
                                else if (stopwatch.ElapsedMilliseconds - lastStatusMs >= 100)
                                {
                                    lastStatusMs = stopwatch.ElapsedMilliseconds;
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        RowCountText = rowText;
                                        TimingText = timeText;
                                    });
                                }
                            }
                        }, ct);
                    }
                    finally
                    {
                        // Runs on the UI thread (the awaiter resumed here) for
                        // success, cancellation, and failure alike, so whatever
                        // was streamed is always what the grid shows.
                        Rows = new AvaloniaList<object?[]>(allRows);
                    }

                    Status = "Done";
                    RowCountText = RowLabel(allRows.Count);
                    TimingText = $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms · first byte {firstByteMs} ms";
                    CapText = truncated
                        ? $"capped at {MaxDisplayRows:N0} rows — refine the query for the full set"
                        : null;
                    break;

                case MaterializedResultSet materialized:
                    // Fully-materialized result of a statement run inside a
                    // transaction (see QueryEngine): no streaming, the rows are
                    // already in memory. Mirror the streaming path's cap handling.
                    _columns = materialized.Columns;
                    foreach (var column in materialized.Columns)
                    {
                        ColumnNames.Add(column.Name);
                    }

                    var overCap = materialized.Truncated || materialized.Rows.Count > MaxDisplayRows;
                    var shown = overCap
                        ? materialized.Rows.Take(MaxDisplayRows).ToList()
                        : materialized.Rows;

                    Rows = new AvaloniaList<object?[]>(shown);
                    Status = "Done";
                    RowCountText = RowLabel(shown.Count);
                    TimingText = $"{materialized.Elapsed.TotalMilliseconds:F0} ms";
                    CapText = overCap
                        ? $"capped at {MaxDisplayRows:N0} rows — refine the query for the full set"
                        : null;
                    break;

                case CommandResult commandResult:
                    Status = commandResult.CommandTag;
                    RowCountText = $"{RowLabel(commandResult.RowsAffected)} affected";
                    TimingText = $"{commandResult.Elapsed.TotalMilliseconds:F0} ms";
                    break;

                case QueryError error:
                    Status = error.RolledBack
                        ? $"Error: {error.Message} — transaction rolled back"
                        : $"Error: {error.Message}";
                    HasError = true;
                    await TryOfferFixAsync(executedSql);
                    break;
            }

            Executed?.Invoke(new QueryHistoryEntry(executedSql, DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalMilliseconds, StatusSummary()));
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
            Executed?.Invoke(new QueryHistoryEntry(executedSql, DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalMilliseconds, StatusSummary()));
        }
        catch (Exception ex)
        {
            // A mid-stream failure the engine couldn't turn into a QueryError
            // (e.g. a dropped connection surfacing while the batches enumerate on
            // the background thread). Surface it in the status bar instead of
            // letting it escape the command and crash the app.
            Status = $"Error: {ex.Message}";
            HasError = true;
            Executed?.Invoke(new QueryHistoryEntry(executedSql, DateTimeOffset.UtcNow, stopwatch.Elapsed.TotalMilliseconds, StatusSummary()));
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Runs each statement of a script sequentially on one shared connection,
    // adding a selectable result section per statement as it lands. The engine
    // enumeration runs on a background thread (ReadAsync often completes
    // synchronously, so consuming it on the UI thread would freeze it and make
    // Cancel unresponsive); section adds are marshaled back to the UI thread.
    private async Task RunScriptAsync(IReadOnlyList<string> statements, Stopwatch stopwatch, CancellationToken ct)
    {
        var index = 0;

        await Task.Run(async () =>
        {
            await foreach (var result in _engine.ExecuteScriptAsync(statements, MaxDisplayRows, ct).WithCancellation(ct))
            {
                index++;
                var section = ScriptResultViewModel.From(index, statements[index - 1], result);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResultSections.Add(section);
                    // Show the first section the moment it arrives.
                    SelectedSection ??= section;
                    NotifyScriptResultChanged();
                });
            }
        }, ct);

        // Surface a failure by jumping to the statement that failed, and let its
        // section (via OnSelectedSectionChanged) leave the real error message and
        // red status in the bar. On success, summarize the whole script instead.
        var firstError = ResultSections.FirstOrDefault(s => s.HasError);
        if (firstError is not null)
        {
            SelectedSection = firstError;
        }
        else
        {
            var count = ResultSections.Count;
            Status = $"Script: {count} statement{(count == 1 ? "" : "s")} OK";
        }

        // The selected section fills the row-count segment; append the whole-script total here.
        TimingText = $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms total";
    }

    // After a failed run, ask the catalog-backed reconciler whether the query's
    // unquoted identifiers map unambiguously to case-differing real names (the
    // classic "typed games.spells, the table is \"Spells\"" miss) and, if so,
    // stage a one-click fix. Best-effort: any failure just leaves no offer.
    private async Task TryOfferFixAsync(string executedSql)
    {
        if (_reconcilerFactory is null)
        {
            return;
        }

        try
        {
            var reconciler = await _reconcilerFactory(CancellationToken.None);
            if (reconciler is not null
                && reconciler.TryReconcile(executedSql, out var fixedSql, out var fixes)
                && fixes.Count > 0)
            {
                FixSuggestionSql = fixedSql;
                FixSuggestionText = fixes.Count == 1
                    ? $"Did you mean {fixes[0].Replacement}?"
                    : $"Quote {fixes.Count} identifiers to match the database?";
            }
        }
        catch
        {
            // No catalog snapshot (e.g. connection dropped): simply offer nothing.
        }
    }

    private bool CanApplyFix() => FixSuggestionSql is not null && !IsRunning;

    /// <summary>Applies the staged identifier fix to the editor and re-runs the query.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyFix))]
    private async Task ApplyFixAsync()
    {
        if (FixSuggestionSql is not { } fixedSql)
        {
            return;
        }

        // Read the fix before assigning Sql — OnSqlChanged clears the suggestion.
        Sql = fixedSql;
        await RunCommand.ExecuteAsync(null);
    }

    partial void OnFixSuggestionSqlChanged(string? value) => ApplyFixCommand.NotifyCanExecuteChanged();

    // Selecting a script section re-points the shared grid and status-bar
    // segments at that statement's materialized result.
    partial void OnSelectedSectionChanged(ScriptResultViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        _columns = value.Columns;
        ColumnNames.Clear();
        foreach (var name in value.ColumnNames)
        {
            ColumnNames.Add(name);
        }

        Rows = value.Rows;
        Status = value.StatusText;
        HasError = value.HasError;
        RowCountText = value.RowCountText;
        TimingText = value.TimingText;
        CapText = value.CapText;
    }

    private void NotifyScriptResultChanged()
    {
        OnPropertyChanged(nameof(IsScriptResult));
        OnPropertyChanged(nameof(HasNoResults));
    }

    private static string RowLabel(long count) => count == 1 ? "1 row" : $"{count:N0} rows";

    /// <summary>Flattens the segmented status back into one line for query-history entries.</summary>
    private string StatusSummary() =>
        string.Join(" · ", new[] { Status, RowCountText, TimingText, CapText }.Where(s => !string.IsNullOrEmpty(s)));

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
        // Own a CTS so the Cancel button (enabled by IsRunning) actually cancels
        // the EXPLAIN, not just SELECTs.
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsRunning = true;
        Status = analyze ? "Running EXPLAIN ANALYZE..." : "Running EXPLAIN...";
        HasError = false;
        // Hide any plan already on screen until this run yields a fresh one, so a
        // failed or cancelled re-run can't leave a stale plan beside the new error.
        IsShowingPlan = false;

        try
        {
            var result = await _explainService.ExplainAsync(Sql, analyze, ct);
            ExplainRoot = new ExplainNodeViewModel(result.Root, result.Root.TotalCost);
            var planningFragment = result.PlanningTimeMs is { } planMs ? $"Planning: {planMs:F3} ms" : null;
            var executionFragment = result.ExecutionTimeMs is { } execMs ? $"Execution: {execMs:F3} ms" : null;
            ExplainSummary = string.Join("   ", new[] { planningFragment, executionFragment }.Where(f => f is not null));
            IsShowingPlan = true;
            Status = "Plan ready";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
        }
        catch (PostgresException ex)
        {
            Status = $"Explain failed: {ex.MessageText}";
            HasError = true;
        }
        catch (Exception ex)
        {
            Status = $"Explain failed: {ex.Message}";
            HasError = true;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ExplainCommand.NotifyCanExecuteChanged();
        ExplainAnalyzeCommand.NotifyCanExecuteChanged();
        ApplyFixCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasNoResults));

        if (value)
        {
            StartRunClock();
        }
        else
        {
            StopRunClock();
        }
    }

    // The live clock runs entirely on the UI thread: IsRunning flips on the UI
    // thread (before the first await, and again in the run's finally, which
    // resumes on the captured UI context), so timer create/start/stop are safe here.
    private void StartRunClock()
    {
        _runClock.Restart();
        RunningElapsedText = "0.0 s";

        _runClockTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _runClockTimer.Tick -= OnRunClockTick;
        _runClockTimer.Tick += OnRunClockTick;
        _runClockTimer.Start();
    }

    private void StopRunClock()
    {
        _runClockTimer?.Stop();
        _runClock.Stop();
        RunningElapsedText = null;
    }

    private void OnRunClockTick(object? sender, EventArgs e) =>
        RunningElapsedText = $"{_runClock.Elapsed.TotalSeconds:F1} s";

    partial void OnRowsChanged(AvaloniaList<object?[]> value) => OnPropertyChanged(nameof(HasNoResults));

    partial void OnIsShowingPlanChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    partial void OnSqlChanged(string value)
    {
        // A manual edit invalidates both the inline-edit mapping and browse
        // mode; a programmatic browse-page compose keeps them.
        if (!_applyingBrowseSql)
        {
            EditContext = null;
            Browse = null;
        }

        // A stale "did you mean…?" offer no longer matches the edited text.
        FixSuggestionSql = null;
        FixSuggestionText = null;

        IsShowingPlan = false;
        IsDirty = !string.Equals(value, _lastRunSql, StringComparison.Ordinal);
        UpdateTabTitle();
    }

    partial void OnBrowseChanged(TableBrowseViewModel? value) => OnPropertyChanged(nameof(IsBrowsing));

    /// <summary>
    /// Enters no-SQL browse mode for a table and loads its first page. The
    /// filter/sort/paging bar (bound to <see cref="Browse"/>) takes over from
    /// there, re-querying the server on every change.
    /// </summary>
    public Task StartBrowseAsync(string schema, string name, IReadOnlyList<string> primaryKeyColumns)
    {
        _browsePkColumns = primaryKeyColumns;
        Browse = new TableBrowseViewModel(schema, name, RunBrowseSqlAsync);
        return Browse.LoadAsync();
    }

    // Runs one composed browse page through the normal streaming/grid path,
    // then re-establishes inline editing for the browsed table (setting Sql
    // cleared it). Returns the displayed row count so paging can advance.
    private async Task<int> RunBrowseSqlAsync(string sql)
    {
        _applyingBrowseSql = true;
        Sql = sql;
        _applyingBrowseSql = false;

        await RunCommand.ExecuteAsync(null);

        if (_browsePkColumns is { Count: > 0 } pk && Browse is { } browse)
        {
            EditContext = new EditableTableContext(browse.Schema, browse.Name, pk);
        }

        return Rows.Count;
    }

    partial void OnDefaultTitleChanged(string value) => UpdateTabTitle();

    partial void OnTitleOverrideChanged(string? value) => UpdateTabTitle();

    // A fixed override wins; otherwise name the tab after the first table the SQL
    // references, falling back to "Query N".
    private void UpdateTabTitle() => TabTitle = TitleOverride ?? DeriveTableName(Sql) ?? DefaultTitle;

    private static string? DeriveTableName(string sql)
    {
        var match = TableReferenceRegex().Match(sql);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups[1].Value;
        // Keep just the table part of a schema-qualified name, and drop any quoting.
        var table = (raw.Contains('.') ? raw[(raw.LastIndexOf('.') + 1)..] : raw).Trim('"');
        return string.IsNullOrEmpty(table) ? null : table;
    }

    // First identifier after FROM/JOIN/UPDATE/INTO (covers SELECT, DELETE FROM, UPDATE, INSERT INTO);
    // captures an optionally-quoted, optionally schema-qualified name. Source-generated for AOT.
    [GeneratedRegex(@"\b(?:from|join|update|into)\s+(""?[\w$]+""?(?:\.""?[\w$]+""?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TableReferenceRegex();

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
        HasError = false;

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
            HasError = true;
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
            HasError = true;
        }
    }

    /// <summary>
    /// Sets a single cell to SQL NULL via a targeted primary-key-keyed
    /// UPDATE. Inline editing can't express NULL (an emptied editor is an
    /// empty string), so this is the explicit gesture behind the grid's
    /// "Set cell to NULL" context-menu action. On success the row is
    /// replaced in place (same replace-not-mutate rule as
    /// <see cref="CommitCellEditAsync"/>).
    /// </summary>
    public async Task SetCellNullAsync(object?[] row, int columnIndex)
    {
        HasError = false;

        if (EditContext is not { } context)
        {
            Status = "Editing isn't available for this result set.";
            return;
        }

        if (columnIndex < 0 || columnIndex >= ColumnNames.Count)
        {
            return;
        }

        var columnName = ColumnNames[columnIndex];

        if (context.PrimaryKeyColumns.Contains(columnName))
        {
            Status = "Primary key columns can't be set to NULL.";
            HasError = true;
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
            SET {SqlIdentifier.Quote(columnName)} = NULL
            WHERE {whereClause}
            """;

        var parameters = new Dictionary<string, object?>();
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
                updated[columnIndex] = null;
                Rows[rowIndex] = updated;
            }

            Status = $"Set {context.Schema}.{context.Table}.{columnName} to NULL";
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
            HasError = true;
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

        // Common Postgres types Npgsql maps to CLR types that don't implement
        // IConvertible (timestamptz→DateTimeOffset, date→DateOnly, time→TimeOnly,
        // interval→TimeSpan). Convert.ChangeType throws on these, so a raw string
        // would fall through and hit a Postgres type-mismatch - parse explicitly.
        if (underlying == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        }

        if (underlying == typeof(DateOnly))
        {
            return DateOnly.Parse(text, CultureInfo.InvariantCulture);
        }

        if (underlying == typeof(TimeOnly))
        {
            return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
        }

        if (underlying == typeof(TimeSpan))
        {
            return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        }

        if (typeof(IConvertible).IsAssignableFrom(underlying))
        {
            return Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>
    /// Deletes the given rows from the mapped table, one targeted
    /// primary-key-keyed DELETE each. In browse mode the page is reloaded
    /// afterward (so paging/counts stay correct and the page refills from the
    /// server); otherwise the rows are dropped from the grid in place. Returns
    /// how many rows were deleted.
    /// </summary>
    public async Task<int> DeleteRowsAsync(IReadOnlyList<object?[]> rows)
    {
        HasError = false;

        if (EditContext is not { PrimaryKeyColumns.Count: > 0 } context)
        {
            Status = "Delete isn't available for this result set.";
            HasError = true;
            return 0;
        }

        var pkIndexes = context.PrimaryKeyColumns.Select(pk => ColumnNames.IndexOf(pk)).ToList();
        if (pkIndexes.Any(i => i < 0))
        {
            Status = "Cannot delete: a primary key column isn't present in this result set.";
            HasError = true;
            return 0;
        }

        var whereClause = string.Join(
            " AND ",
            context.PrimaryKeyColumns.Select((pk, n) => $"{SqlIdentifier.Quote(pk)} = @pk{n}"));

        var sql = $"DELETE FROM {SqlIdentifier.Quote(context.Schema)}.{SqlIdentifier.Quote(context.Table)} WHERE {whereClause}";

        var deleted = 0;
        try
        {
            foreach (var row in rows)
            {
                var parameters = new Dictionary<string, object?>();
                for (var n = 0; n < pkIndexes.Count; n++)
                {
                    parameters[$"pk{n}"] = row[pkIndexes[n]];
                }

                await _engine.ExecuteNonQueryAsync(sql, parameters, CancellationToken.None);
                deleted++;

                if (Browse is null)
                {
                    Rows.Remove(row);
                }
            }

            Status = deleted == 1 ? "Deleted 1 row" : $"Deleted {deleted:N0} rows";
        }
        catch (Exception ex)
        {
            Status = deleted == 0
                ? $"Delete failed: {ex.Message}"
                : $"Delete failed after {deleted:N0} row(s): {ex.Message}";
            HasError = true;
        }

        if (Browse is { } browse)
        {
            await browse.LoadAsync();
        }
        else
        {
            RowCountText = RowLabel(Rows.Count);
        }

        return deleted;
    }

    /// <summary>Reloads whatever the grid currently shows — the browse page if browsing, else the last query — after an out-of-band change (e.g. a row insert).</summary>
    public Task RefreshCurrentAsync() =>
        Browse is { } browse ? browse.LoadAsync() : RunCommand.ExecuteAsync(null);

    /// <summary>
    /// Snapshots the current result (columns + rows) and returns a writer that
    /// renders it as CSV. The snapshot is taken synchronously on the calling
    /// (UI) thread, so the returned delegate is safe to run on a background
    /// thread — a large export then writes without freezing the UI or racing a
    /// concurrent grid mutation.
    /// </summary>
    public Action<Stream> CreateCsvExport()
    {
        var columns = ColumnNames.ToList();
        var rows = Rows.ToList();
        return stream =>
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            ResultExporter.WriteCsv(writer, columns, rows);
        };
    }

    /// <summary>JSON counterpart of <see cref="CreateCsvExport"/> — snapshots on the UI thread, writes off it.</summary>
    public Action<Stream> CreateJsonExport()
    {
        var columns = ColumnNames.ToList();
        var rows = Rows.ToList();
        return stream => ResultExporter.WriteJson(stream, columns, rows);
    }

    /// <summary>The clipboard "Copy as" shapes the results grid offers.</summary>
    public enum CopyFormat
    {
        Tsv,
        Csv,
        Json,
        Markdown,
        Insert,
    }

    /// <summary>
    /// Renders the given rows (or the whole result set when <paramref name="selectedRows"/> is empty) in
    /// <paramref name="format"/> for the clipboard. Returns null when there's nothing to copy. INSERT statements
    /// target the edited table when the result set maps to one, otherwise a <c>table_name</c> placeholder.
    /// </summary>
    public string? CopyRows(CopyFormat format, IReadOnlyList<object?[]> selectedRows)
    {
        var rows = selectedRows.Count > 0 ? selectedRows : (IReadOnlyList<object?[]>)Rows;
        if (rows.Count == 0 || ColumnNames.Count == 0)
        {
            return null;
        }

        using var writer = new StringWriter();
        switch (format)
        {
            case CopyFormat.Tsv:
                ResultExporter.WriteTsv(writer, ColumnNames, rows);
                break;
            case CopyFormat.Csv:
                ResultExporter.WriteCsv(writer, ColumnNames, rows);
                break;
            case CopyFormat.Markdown:
                ResultExporter.WriteMarkdown(writer, ColumnNames, rows);
                break;
            case CopyFormat.Insert:
                ResultExporter.WriteInsert(writer, InsertTargetTable, ColumnNames, rows);
                break;
            case CopyFormat.Json:
                using (var stream = new MemoryStream())
                {
                    ResultExporter.WriteJson(stream, ColumnNames, rows);
                    return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
        }

        return writer.ToString();
    }

    private string InsertTargetTable => EditContext is { } ctx
        ? $"{ResultExporter.QuoteIdentifier(ctx.Schema)}.{ResultExporter.QuoteIdentifier(ctx.Table)}"
        : "table_name";
}
