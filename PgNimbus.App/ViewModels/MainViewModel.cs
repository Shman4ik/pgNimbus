using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.App.Completion;
using PgNimbus.Core.Import;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Settings;

namespace PgNimbus.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly QueryEngine _engine;
    private readonly ExplainService _explainService;
    private readonly SchemaService _schemaService;
    private readonly SchemaEditor _schemaEditor;
    private readonly DdlService _ddlService;

    public SchemaTreeViewModel SchemaTree { get; }

    public SqlCompletionProvider CompletionProvider { get; }

    public SavedQueriesViewModel SavedQueries { get; }

    public NotifyMonitorViewModel NotifyMonitor { get; }

    /// <summary>Backs the Server Activity window (pg_stat_activity live view).</summary>
    public ActivityViewModel Activity { get; }

    /// <summary>Backs the Database Overview window (sizes, cache-hit, scan usage, unused indexes).</summary>
    public DatabaseOverviewViewModel DatabaseOverview { get; }

    /// <summary>COPY-based CSV/JSON loader behind the Import dialog (the view constructs the dialog's ViewModel from it).</summary>
    public ImportService Importer { get; }

    public CommandPaletteViewModel CommandPalette { get; } = new();

    public CellInspectorViewModel CellInspector { get; } = new();

    // Palette actions that need the window (theme, dialogs) live in the view;
    // MainWindow subscribes to these so the palette can trigger them.
    public event Action? ThemeToggleRequested;
    public event Action? ShortcutsRequested;
    // Raised when the user asks to connect to a different server/database;
    // MainWindow reopens the connection dialog (App.BuildConnectionDialog).
    public event Action? SwitchConnectionRequested;
    // Raised to open another connection side by side; MainWindow opens the
    // connection dialog additively — the current window stays connected and open.
    public event Action? NewWindowRequested;
    // Raised to pretty-print the statement under the caret; MainWindow owns the
    // editor text (AvaloniaEdit's Text isn't bindable) so it does the rewrite.
    public event Action? FormatSqlRequested;
    // Raised to replace the statement's SELECT * with the explicit column
    // list; MainWindow applies the rewrite, same split as Format SQL.
    public event Action? ExpandStarRequested;
    // Raised to open the editor's find / find & replace panel (the view owns
    // the AvaloniaEdit SearchPanel). The bool is "replace mode".
    public event Action<bool>? FindRequested;
    // Raised to open the "paste an EXPLAIN plan" dialog; MainWindow owns the modal
    // and calls OpenImportedPlan on success (no DB round-trip).
    public event Action? ImportPlanRequested;
    // Raised to open (or focus) the Server Activity window, which the view owns.
    public event Action? ActivityRequested;
    // Raised to open (or focus) the Database Overview window, which the view owns.
    public event Action? DatabaseOverviewRequested;
    // Raised to collapse/restore the sidebar (the view owns the grid column).
    public event Action? SidebarToggleRequested;
    // Raised to open (or focus) the preferences window, which the view owns.
    public event Action? PreferencesRequested;
    // Raised to open the "Open SQL file" picker; MainWindow owns the
    // StorageProvider dialog and file I/O.
    public event Action? OpenFileRequested;
    // Raised to save the active tab's SQL to disk; true = "save as" (always
    // prompt), false = save-in-place (prompt only when the tab has no file yet).
    public event Action<bool>? SaveFileRequested;
    // Raised to open a specific recent file (from the palette's "Recent file" entries).
    public event Action<string>? OpenRecentFileRequested;

    [RelayCommand]
    private void OpenFile() => OpenFileRequested?.Invoke();

    [RelayCommand]
    private void SaveFile() => SaveFileRequested?.Invoke(false);

    [RelayCommand]
    private void SaveFileAs() => SaveFileRequested?.Invoke(true);

    [RelayCommand]
    private void SwitchConnection() => SwitchConnectionRequested?.Invoke();

    [RelayCommand]
    private void OpenNewWindow() => NewWindowRequested?.Invoke();

    [RelayCommand]
    private void ShowPreferences() => PreferencesRequested?.Invoke();

    /// <summary>
    /// Flips auto-alias and reports the new state in the status bar — the
    /// setting has no always-visible indicator, so a hotkey/palette toggle
    /// needs some visible confirmation.
    /// </summary>
    [RelayCommand]
    private void ToggleAutoAlias()
    {
        AutoAliasTables = !AutoAliasTables;
        ActiveTab.Status = AutoAliasTables
            ? "Auto-alias tables: on (orders → orders o)"
            : "Auto-alias tables: off";
    }

    /// <summary>
    /// Flips safe mode and reports the new state in the status bar (same
    /// visible-confirmation rationale as <see cref="ToggleAutoAlias"/>).
    /// Changes already staged stay staged either way — they only leave via
    /// commit or discard.
    /// </summary>
    [RelayCommand]
    private void ToggleSafeMode()
    {
        SafeModeEdits = !SafeModeEdits;
        ActiveTab.Status = SafeModeEdits
            ? "Safe mode: on — grid edits, deletes, and inserts are staged for review"
            : "Safe mode: off — grid changes apply immediately";
    }

    [RelayCommand]
    private void ShowActivity() => ActivityRequested?.Invoke();

    [RelayCommand]
    private void ShowDatabaseOverview() => DatabaseOverviewRequested?.Invoke();

    // Relations rarely change mid-session, so the palette's "jump to a table"
    // list is fetched once and reused across opens.
    private IReadOnlyList<RelationInfo>? _relationCache;

    // Catalog-name snapshot for the unquoted-identifier fix, built lazily on the
    // first failed query and reused until the schema is refreshed.
    private IdentifierReconciler? _reconciler;

    /// <summary>The connected profile's accent color ("#RRGGBB"), or null. Lets the window chrome show at a glance which environment (e.g. prod vs. dev) is connected.</summary>
    public string? AccentColor { get; }

    /// <summary>Server host for the title-bar breadcrumb (host › database).</summary>
    public string ConnectionHost { get; }

    /// <summary>Database name for the title-bar breadcrumb (host › database).</summary>
    public string ConnectionDatabase { get; }

    public ObservableCollection<QueryViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private QueryViewModel _activeTab = null!;

    /// <summary>
    /// True while an explicit BEGIN…COMMIT/ROLLBACK transaction is open — drives
    /// the toolbar's "in transaction" indicator and which transaction buttons
    /// show. Kept in sync with the engine (including auto-rollback on error) via
    /// its <see cref="QueryEngine.TransactionStateChanged"/> event.
    /// </summary>
    [ObservableProperty]
    private bool _isInTransaction;

    /// <summary>
    /// Whether accepting a table from completion after FROM/JOIN also appends a
    /// short alias. Toggled from the command palette; persisted via the
    /// callback so the choice survives a restart (same pattern as the sidebar's
    /// advanced-objects toggle).
    /// </summary>
    [ObservableProperty]
    private bool _autoAliasTables;

    private readonly Action<bool>? _persistAutoAliasTables;

    partial void OnAutoAliasTablesChanged(bool value) => _persistAutoAliasTables?.Invoke(value);

    /// <summary>
    /// Safe mode: grid edits, deletes, and Add-row inserts are staged locally
    /// for review instead of executing immediately (see
    /// <see cref="QueryViewModel.PendingChanges"/>). Toggled from the command
    /// palette or preferences; persisted like <see cref="AutoAliasTables"/>.
    /// </summary>
    [ObservableProperty]
    private bool _safeModeEdits;

    private readonly Action<bool>? _persistSafeModeEdits;

    partial void OnSafeModeEditsChanged(bool value) => _persistSafeModeEdits?.Invoke(value);

    /// <summary>
    /// Notepad++-style word wrap in the SQL editor. Bound two-way to the editor's
    /// <c>WordWrap</c> and to the command-bar toggle; persisted like the other
    /// editor toggles so the choice survives a restart.
    /// </summary>
    [ObservableProperty]
    private bool _wordWrapEditor;

    private readonly Action<bool>? _persistWordWrapEditor;

    // Most-recently-opened/saved .sql file paths, most recent first, capped at
    // 10 — backs the palette's "Recent file" entries. Kept as a plain list
    // (not observable) since the palette only reads it when it (re)builds its
    // candidate set, not live.
    private readonly List<string> _recentSqlFiles;

    private readonly Action<IReadOnlyList<string>>? _persistRecentSqlFiles;

    /// <summary>The recent .sql files, most recent first — read by the app menu's "Open recent" submenu (the palette reads <see cref="_recentSqlFiles"/> directly).</summary>
    public IReadOnlyList<string> RecentSqlFiles => _recentSqlFiles;

    // Persist and report the new state here rather than in ToggleWordWrap, so
    // the status line updates the same way whether wrap was flipped from the
    // palette command or by the toolbar toggle's direct two-way binding.
    // ActiveTab is set in the constructor, but guard anyway for a transient null
    // during tab transitions.
    partial void OnWordWrapEditorChanged(bool value)
    {
        _persistWordWrapEditor?.Invoke(value);
        if (ActiveTab is not null)
        {
            ActiveTab.Status = value ? "Word wrap: on" : "Word wrap: off";
        }
    }

    /// <summary>
    /// Pretty-prints the statement under the caret. The rewrite itself lives in
    /// the view (AvaloniaEdit's Text isn't bindable), so this just raises the
    /// event — same target as the palette's "Format SQL" and the editor's
    /// Alt+Shift+F shortcut.
    /// </summary>
    [RelayCommand]
    private void FormatSql() => FormatSqlRequested?.Invoke();

    /// <summary>
    /// Flips word wrap. The status-line update + persistence ride on the
    /// <see cref="OnWordWrapEditorChanged"/> hook, so the toolbar toggle (which
    /// binds the property directly) stays consistent with this command.
    /// </summary>
    [RelayCommand]
    private void ToggleWordWrap() => WordWrapEditor = !WordWrapEditor;

    public MainViewModel(
        QueryEngine engine,
        ExplainService explainService,
        SchemaTreeViewModel schemaTree,
        SchemaService schemaService,
        SchemaEditor schemaEditor,
        DdlService ddlService,
        SqlCompletionProvider completionProvider,
        NotifyMonitorViewModel notifyMonitor,
        ActivityService activityService,
        DatabaseStatsService databaseStatsService,
        ImportService importService,
        string? accentColor = null,
        string connectionHost = "",
        string connectionDatabase = "",
        bool autoAliasTables = true,
        Action<bool>? persistAutoAliasTables = null,
        bool safeModeEdits = true,
        Action<bool>? persistSafeModeEdits = null,
        bool wordWrapEditor = false,
        Action<bool>? persistWordWrapEditor = null,
        WorkspaceEntry? workspace = null,
        IReadOnlyList<string>? recentSqlFiles = null,
        Action<IReadOnlyList<string>>? persistRecentSqlFiles = null)
    {
        ConnectionHost = connectionHost;
        ConnectionDatabase = connectionDatabase;
        _autoAliasTables = autoAliasTables;
        _persistAutoAliasTables = persistAutoAliasTables;
        _safeModeEdits = safeModeEdits;
        _persistSafeModeEdits = persistSafeModeEdits;
        _wordWrapEditor = wordWrapEditor;
        _persistWordWrapEditor = persistWordWrapEditor;
        _recentSqlFiles = recentSqlFiles is null ? [] : recentSqlFiles.ToList();
        _persistRecentSqlFiles = persistRecentSqlFiles;
        _engine = engine;
        _explainService = explainService;
        SchemaTree = schemaTree;
        // Wire the schema tree's window-level actions (context menu, double-click,
        // full refresh) so the SchemaTreePanel can invoke them through its own
        // sub-ViewModel without reaching back into this host — see the callbacks'
        // docs on SchemaTreeViewModel.
        SchemaTree.RefreshAllRequested = RefreshSchemaAsync;
        SchemaTree.ShowTableSourceRequested = ShowSourceAsync;
        SchemaTree.PreviewTableRequested = PreviewTableAsync;
        SchemaTree.ShowFunctionSourceRequested = ShowFunctionSourceAsync;
        SchemaTree.SetExtensionInstalledRequested = SetExtensionInstalledAsync;
        SchemaTree.AlterTableViewModelFactory = CreateAlterTableViewModel;
        _schemaService = schemaService;
        _schemaEditor = schemaEditor;
        _ddlService = ddlService;
        CompletionProvider = completionProvider;
        SavedQueries = new SavedQueriesViewModel(
            new SavedQueryStore(),
            new QueryHistoryStore(),
            () => ActiveTab,
            (title, sql) =>
            {
                var tab = NewTab();
                tab.TitleOverride = title;
                tab.Sql = sql;
            },
            // History entries are stamped with this label for per-connection scoping.
            () => string.IsNullOrEmpty(ConnectionHost) ? null : $"{ConnectionHost}/{ConnectionDatabase}");
        NotifyMonitor = notifyMonitor;
        Activity = new ActivityViewModel(activityService);
        DatabaseOverview = new DatabaseOverviewViewModel(databaseStatsService);
        Importer = importService;
        AccentColor = accentColor;

        // The engine owns the transaction state; mirror it here so the indicator
        // and command availability follow every change — including an
        // auto-rollback that fires from a background query thread.
        _engine.TransactionStateChanged += OnEngineTransactionStateChanged;

        // Restore the last session's tabs for this connection, if any. Browse-mode
        // tabs (table/function "source" views opened via ShowSourceAsync etc.) are
        // deliberately restored as their composed page SQL - i.e. plain query
        // tabs - rather than as live browse sessions; there is no saved browse
        // state to reconstruct from.
        if (workspace is { Tabs.Count: > 0 })
        {
            foreach (var saved in workspace.Tabs)
            {
                var tab = NewTab();
                tab.Sql = saved.Sql;
                tab.TitleOverride = saved.Title;

                // Best-effort reattach to the tab's saved file association. The
                // restored buffer (saved.Sql, just set above) is kept as-is —
                // AttachFile only sets the disk-comparison baseline, not Sql —
                // so if the buffer and the file have since diverged (edited here
                // but not saved, or the file changed elsewhere), the dirty dot
                // honestly reflects that the moment the tab reopens. If the file
                // is gone or unreadable, this just leaves the tab as a titled
                // scratch tab — restore must never fail the whole session over it.
                if (saved.FilePath is { } filePath)
                {
                    try
                    {
                        var diskContent = File.ReadAllText(filePath);
                        tab.AttachFile(filePath, diskContent);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Leave as a titled scratch tab (TitleOverride from above still applies).
                    }
                }
            }

            var activeIndex = Math.Clamp(workspace.ActiveTabIndex, 0, Tabs.Count - 1);
            ActiveTab = Tabs[activeIndex];
        }
        else
        {
            AddTab();
        }
    }

    private void OnEngineTransactionStateChanged() =>
        Dispatcher.UIThread.Post(() => IsInTransaction = _engine.IsInTransaction);

    partial void OnIsInTransactionChanged(bool value)
    {
        BeginTransactionCommand.NotifyCanExecuteChanged();
        CommitTransactionCommand.NotifyCanExecuteChanged();
        RollbackTransactionCommand.NotifyCanExecuteChanged();
    }

    private bool CanBeginTransaction() => !IsInTransaction;

    private bool CanEndTransaction() => IsInTransaction;

    /// <summary>Opens an explicit transaction (BEGIN); subsequent statements run inside it until commit or rollback.</summary>
    [RelayCommand(CanExecute = nameof(CanBeginTransaction))]
    private async Task BeginTransactionAsync()
    {
        try
        {
            await _engine.BeginTransactionAsync(CancellationToken.None);
            ActiveTab.Status = "Transaction started — BEGIN";
        }
        catch (Exception ex)
        {
            ActiveTab.Status = $"Couldn't begin transaction: {ex.Message}";
            ActiveTab.HasError = true;
        }
    }

    /// <summary>Commits the open transaction (COMMIT).</summary>
    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private async Task CommitTransactionAsync()
    {
        try
        {
            await _engine.CommitAsync(CancellationToken.None);
            ActiveTab.Status = "Transaction committed — COMMIT";
        }
        catch (Exception ex)
        {
            ActiveTab.Status = $"Commit failed: {ex.Message}";
            ActiveTab.HasError = true;
        }
    }

    /// <summary>Rolls back the open transaction (ROLLBACK).</summary>
    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private async Task RollbackTransactionAsync()
    {
        try
        {
            await _engine.RollbackAsync(CancellationToken.None);
            ActiveTab.Status = "Transaction rolled back — ROLLBACK";
        }
        catch (Exception ex)
        {
            ActiveTab.Status = $"Rollback failed: {ex.Message}";
            ActiveTab.HasError = true;
        }
    }

    // Lazily builds (and caches) the reconciler each query tab uses to offer an
    // identifier fix after a failed run. One catalog round trip, amortized across
    // tabs and reruns; invalidated by RefreshSchemaAsync.
    private async Task<IdentifierReconciler?> GetReconcilerAsync(CancellationToken ct)
    {
        if (_reconciler is not null)
        {
            return _reconciler;
        }

        var names = await _schemaService.GetCatalogNamesAsync(ct);
        return _reconciler = new IdentifierReconciler(names);
    }

    public AlterTableViewModel CreateAlterTableViewModel(TableNode table) =>
        new(_schemaEditor, _schemaService, table.Schema, table.Name);

    public AddRowViewModel CreateAddRowViewModel(string schema, string table, Func<IReadOnlyList<PendingInsertValue>, string?>? stageInsert = null) =>
        new(_engine, _schemaService, schema, table, stageInsert);

    /// <summary>
    /// Reloads everything derived from the live catalog — the schema tree, the
    /// autocomplete cache, and the command palette's table list — so objects
    /// created or altered in another session (or via a DDL statement here) show
    /// up without reconnecting.
    /// </summary>
    [RelayCommand]
    private async Task RefreshSchemaAsync()
    {
        // Force the palette to re-fetch relations, the fix reconciler to
        // rebuild its name snapshot, and the FK edges to reload, on next use.
        _relationCache = null;
        _reconciler = null;
        _foreignKeyCache = null;

        await Task.WhenAll(
            SchemaTree.RefreshCommand.ExecuteAsync(null),
            CompletionProvider.RefreshAsync(CancellationToken.None),
            EnsureForeignKeysAsync());
    }

    private bool CanCloseTab() => Tabs.Count > 1;

    [RelayCommand]
    private void AddTab() => NewTab();

    // Creates a query tab, wires its history hook, and makes it active.
    private QueryViewModel NewTab()
    {
        var tab = new QueryViewModel(_engine, _explainService, GetReconcilerAsync, () => SafeModeEdits, _schemaService) { DefaultTitle = $"Query {Tabs.Count + 1}" };
        tab.Executed += SavedQueries.RecordExecution;
        Tabs.Add(tab);
        ActiveTab = tab;
        CloseTabCommand.NotifyCanExecuteChanged();
        return tab;
    }

    /// <summary>
    /// Moves <paramref name="tab"/> to <paramref name="newIndex"/> (clamped) —
    /// the tab strip's drag-reorder. The moved tab stays active, and the new
    /// order persists naturally: the workspace snapshot serializes
    /// <see cref="Tabs"/> in collection order.
    /// </summary>
    public void MoveTab(QueryViewModel tab, int newIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (oldIndex < 0 || newIndex == oldIndex)
        {
            return;
        }

        Tabs.Move(oldIndex, newIndex);
        // Re-assert: a collection Move can churn the ListBox's selection.
        ActiveTab = tab;
    }

    /// <summary>
    /// Opens <paramref name="path"/> (already read as <paramref name="content"/>
    /// by the caller — MainWindow owns the file I/O) into a query tab. If the
    /// file is already open in some tab (matched by <see cref="QueryViewModel.FilePath"/>,
    /// ordinal), that tab is made active instead of opening a duplicate.
    /// Otherwise a brand-new tab is created — never the active one, per the
    /// "loading a query never overwrites the active tab" rule — and recorded
    /// as the most recent file.
    /// </summary>
    public QueryViewModel OpenFileTab(string path, string content)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.Ordinal));
        if (existing is not null)
        {
            ActiveTab = existing;
            return existing;
        }

        var tab = NewTab();
        tab.Sql = content;
        tab.AttachFile(path, content);
        RecordRecentFile(path);
        return tab;
    }

    /// <summary>
    /// Moves <paramref name="path"/> to the front of the recent-files list
    /// (removing any existing occurrence first), trims to the 10 most recent,
    /// and persists the result. Called on a successful open or save.
    /// </summary>
    public void RecordRecentFile(string path)
    {
        _recentSqlFiles.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
        _recentSqlFiles.Insert(0, path);
        if (_recentSqlFiles.Count > 10)
        {
            _recentSqlFiles.RemoveRange(10, _recentSqlFiles.Count - 10);
        }

        _persistRecentSqlFiles?.Invoke(_recentSqlFiles);
    }

    /// <summary>
    /// Reconstructs a relation's <c>CREATE …</c> definition from pg_catalog and
    /// opens it in a new query tab (its "Source"), where it can be read, copied,
    /// or tweaked and run.
    /// </summary>
    public async Task ShowSourceAsync(TableNode table)
    {
        var ddl = await _ddlService.GenerateAsync(table.Schema, table.Name, CancellationToken.None);

        var tab = NewTab();
        tab.TitleOverride = $"{table.Name} · source";
        tab.Sql = ddl;
    }

    /// <summary>Opens pg_get_functiondef's stored definition of a function/procedure in a new tab.</summary>
    public async Task ShowFunctionSourceAsync(FunctionNode function)
    {
        var ddl = await _ddlService.GenerateFunctionAsync(function.Schema, function.Name, function.Arguments, CancellationToken.None);

        var tab = NewTab();
        tab.TitleOverride = $"{function.Name} · source";
        tab.Sql = ddl;
    }

    /// <summary>
    /// Opens an imported plan (parsed from pasted EXPLAIN JSON/text, no DB round-trip)
    /// in a new tab showing the plan views + warnings strip — never overwriting the
    /// active tab, per the "loading never overwrites" rule.
    /// </summary>
    public void OpenImportedPlan(ImportedPlan plan)
    {
        var tab = NewTab();
        tab.TitleOverride = "Imported plan";
        tab.ShowImportedPlan(plan.Result, plan.DisplayText);
    }

    /// <summary>CREATE/DROP EXTENSION, then reload the Extensions group so the list reflects reality. Errors land in the sidebar's message strip.</summary>
    public async Task SetExtensionInstalledAsync(ExtensionNode extension, bool install)
    {
        SchemaTree.ErrorMessage = null;
        try
        {
            if (install)
            {
                await _schemaEditor.CreateExtensionAsync(extension.Name, CancellationToken.None);
            }
            else
            {
                await _schemaEditor.DropExtensionAsync(extension.Name, CancellationToken.None);
            }

            await extension.Group.RefreshAsync();
        }
        catch (Exception ex)
        {
            SchemaTree.ErrorMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private void CloseTab(QueryViewModel? tab)
    {
        tab ??= ActiveTab;
        if (Tabs.Count <= 1)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // Decide before the removal: when the removed item is the ListBox's
        // selection, RemoveAt makes the two-way SelectedItem binding push
        // ActiveTab = null synchronously, so comparing afterwards misses.
        var wasActive = ReferenceEquals(ActiveTab, tab);

        // A query still running in the closed tab would otherwise keep streaming
        // in the background, holding a pool connection and server-side work for a
        // result nothing will show — cancel it as the tab goes away.
        tab.CancelCommand.Execute(null);

        tab.Executed -= SavedQueries.RecordExecution;
        Tabs.RemoveAt(index);

        if (wasActive || ActiveTab is null)
        {
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        CloseTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NextTab() => CycleTab(+1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    private void CycleTab(int direction)
    {
        if (Tabs.Count < 2)
        {
            return;
        }

        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index + direction + Tabs.Count) % Tabs.Count];
    }

    public Task PreviewTableAsync(TableNode table) => PreviewTableAsync(table.Schema, table.Name);

    public async Task PreviewTableAsync(string schema, string name, string? initialFilter = null)
    {
        // Opens in a new tab rather than the active one - see the "loading a
        // query never overwrites the active tab" rule (CLAUDE.md).
        var tab = NewTab();
        tab.TitleOverride = name;

        var columns = await _schemaService.GetColumnsAsync(schema, name, CancellationToken.None);

        // Open the table in no-SQL browse mode: server-side filter/sort/paging,
        // with inline editing when the table has a primary key. The column
        // metadata rides along so the grid can offer type-aware cell editors.
        await tab.StartBrowseAsync(schema, name, columns, initialFilter);
    }

    // FK edges for grid navigation ("Follow foreign key" / "Referencing rows"
    // on the results-grid context menu). Fetched once in the background at
    // attach time (the menu reads whatever is cached — it can't await), and
    // invalidated with the other catalog caches by RefreshSchemaAsync.
    private IReadOnlyList<ForeignKeyInfo>? _foreignKeyCache;

    /// <summary>The cached FK edge list; empty until <see cref="EnsureForeignKeysAsync"/> has completed once.</summary>
    public IReadOnlyList<ForeignKeyInfo> ForeignKeys => _foreignKeyCache ?? [];

    /// <summary>Loads the FK edges if not cached yet. Best-effort: a failure just leaves grid FK navigation unavailable.</summary>
    public async Task EnsureForeignKeysAsync()
    {
        if (_foreignKeyCache is not null)
        {
            return;
        }

        try
        {
            _foreignKeyCache = await _schemaService.GetForeignKeysAsync(CancellationToken.None);
        }
        catch
        {
            // No connection / query failure: the context-menu items simply stay hidden.
        }
    }

    /// <summary>
    /// Opens the command palette. Actions and saved queries are available
    /// instantly; the (potentially larger) table list is fetched in the
    /// background and merged in without blocking the palette from showing.
    /// </summary>
    public async Task OpenCommandPaletteAsync()
    {
        var baseItems = BuildActionItems().Concat(BuildRecentFileItems()).Concat(BuildSavedQueryItems()).ToList();
        CommandPalette.Open(baseItems);

        try
        {
            _relationCache ??= await _schemaService.GetAllRelationsAsync(CancellationToken.None);
        }
        catch
        {
            // No connection / query failure: the palette still works for
            // actions and saved queries, just without table jumps.
            return;
        }

        if (!CommandPalette.IsOpen)
        {
            return; // dismissed before the tables arrived
        }

        CommandPalette.SetItems(baseItems.Concat(BuildTableItems(_relationCache)).ToList());
    }

    private IEnumerable<PaletteItem> BuildActionItems()
    {
        yield return new PaletteItem("Run query", "Action", "▶", Invoke(() => ActiveTab.RunCommand), Hotkeys.Label("Enter"));
        yield return new PaletteItem("Cancel query", "Action", "■", Invoke(() => ActiveTab.CancelCommand), "Esc");
        yield return new PaletteItem("Explain", "Action", "⚡", Invoke(() => ActiveTab.ExplainCommand));
        yield return new PaletteItem("Explain Analyze", "Action", "⚡", Invoke(() => ActiveTab.ExplainAnalyzeCommand));
        yield return new PaletteItem("Import query plan (paste EXPLAIN JSON or text)…", "Action", "⭳", () => { ImportPlanRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Begin transaction", "Action", "⛃", Invoke(() => BeginTransactionCommand));
        yield return new PaletteItem("Commit transaction", "Action", "✓", Invoke(() => CommitTransactionCommand));
        yield return new PaletteItem("Rollback transaction", "Action", "↺", Invoke(() => RollbackTransactionCommand));
        yield return new PaletteItem("Refresh database & schema", "Action", "⟳", Invoke(() => RefreshSchemaCommand), Hotkeys.Label("Shift+R"));
        yield return new PaletteItem("Server activity", "Action", "∿", () => { ActivityRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Database overview (sizes, cache hit, unused indexes)", "Action", "▦", () => { DatabaseOverviewRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("New query tab", "Action", "＋", Invoke(() => AddTabCommand), Hotkeys.Label("T"));
        yield return new PaletteItem("Close tab", "Action", "✕", Invoke(() => CloseTabCommand), Hotkeys.Label("W"));
        yield return new PaletteItem("Next tab", "Action", "›", Invoke(() => NextTabCommand), Hotkeys.Label("PgDn"));
        yield return new PaletteItem("Previous tab", "Action", "‹", Invoke(() => PreviousTabCommand), Hotkeys.Label("PgUp"));
        yield return new PaletteItem("Format SQL", "Action", "❖", Invoke(() => FormatSqlCommand), $"{Hotkeys.Label("Shift+F")} / Alt+Shift+F");
        yield return new PaletteItem("Toggle word wrap (Notepad++ style)", "Action", "↩", Invoke(() => ToggleWordWrapCommand));
        yield return new PaletteItem("Find in editor", "Action", "⌕", () => { FindRequested?.Invoke(false); return Task.CompletedTask; }, Hotkeys.Label("F"));
        yield return new PaletteItem("Find & replace in editor", "Action", "⌕", () => { FindRequested?.Invoke(true); return Task.CompletedTask; }, Hotkeys.Label("H"));
        yield return new PaletteItem("Expand SELECT * into columns", "Action", "✳", () => { ExpandStarRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Toggle sidebar", "Action", "◫", () => { SidebarToggleRequested?.Invoke(); return Task.CompletedTask; }, Hotkeys.Label("B"));
        yield return new PaletteItem("Toggle auto-alias tables (orders → orders o)", "Action", "a", Invoke(() => ToggleAutoAliasCommand), Hotkeys.Label("Shift+A"));
        yield return new PaletteItem("Toggle safe mode (stage grid changes, review & commit)", "Action", "⛨", Invoke(() => ToggleSafeModeCommand));
        yield return new PaletteItem("Switch connection…", "Action", "⇄", () => { SwitchConnectionRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Open connection in new window…", "Action", "⧉", Invoke(() => OpenNewWindowCommand));
        yield return new PaletteItem("Toggle light/dark theme", "Action", "◐", () => { ThemeToggleRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Preferences…", "Action", "⚙", Invoke(() => ShowPreferencesCommand), Hotkeys.Label(","));
        yield return new PaletteItem("Keyboard shortcuts", "Action", "?", () => { ShortcutsRequested?.Invoke(); return Task.CompletedTask; }, "F1");
        yield return new PaletteItem("Open .sql file…", "Action", "↥", Invoke(() => OpenFileCommand), Hotkeys.Label("O"));
        yield return new PaletteItem("Save tab to file", "Action", "↧", Invoke(() => SaveFileCommand), Hotkeys.Label("S"));
        yield return new PaletteItem("Save tab as…", "Action", "↧", Invoke(() => SaveFileAsCommand), Hotkeys.Label("Shift+S"));
    }

    private IEnumerable<PaletteItem> BuildSavedQueryItems() =>
        SavedQueries.SavedQueries.Select(q => new PaletteItem(
            q.Name,
            "Saved query",
            "★",
            () => { SavedQueries.LoadSavedQueryCommand.Execute(q); return Task.CompletedTask; }));

    // One entry per recent .sql file, most-recent-first (the order they're
    // already kept in). The directory rides in the Shortcut slot (rendered as
    // quiet trailing text in the palette row) since the title alone is just
    // the file name and the full path is otherwise invisible.
    private IEnumerable<PaletteItem> BuildRecentFileItems() =>
        _recentSqlFiles.Select(path => new PaletteItem(
            Path.GetFileName(path),
            "Recent file",
            "▢",
            () => { OpenRecentFileRequested?.Invoke(path); return Task.CompletedTask; },
            Path.GetDirectoryName(path)));

    private IEnumerable<PaletteItem> BuildTableItems(IReadOnlyList<RelationInfo> relations) =>
        relations.Select(r => new PaletteItem(
            $"{r.Schema}.{r.Name}",
            "Table",
            GlyphFor(r.Kind),
            () => PreviewTableAsync(r.Schema, r.Name)));

    private static string GlyphFor(RelationKind kind) => kind switch
    {
        RelationKind.Table => "▤",
        RelationKind.View => "▥",
        RelationKind.MaterializedView => "▦",
        RelationKind.PartitionedTable => "▧",
        _ => "▤",
    };

    // A command may be null at build time (ActiveTab settles later); resolve it
    // lazily at invoke time and only fire when it can execute.
    private static Func<Task> Invoke(Func<System.Windows.Input.ICommand?> resolve) => () =>
    {
        var command = resolve();
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }

        return Task.CompletedTask;
    };
}
