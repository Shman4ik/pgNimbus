using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels.Security;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Import;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Security;
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

    /// <summary>Backs the Roles &amp; Permissions window (roles, grants, default privileges, RLS).</summary>
    public SecurityViewModel Security { get; }

    /// <summary>COPY-based CSV/JSON loader behind the Import dialog (the view constructs the dialog's ViewModel from it).</summary>
    public ImportService Importer { get; }

    public CommandPaletteViewModel CommandPalette { get; } = new();

    public CellInspectorViewModel CellInspector { get; } = new();

    // Palette actions that need the window (theme, dialogs) live in the view;
    // MainWindow subscribes to these so the palette can trigger them.
    public event Action? ThemeToggleRequested;
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

    // Raised to open (or focus) the Roles & Permissions window.
    public event Action? SecurityRequested;
    // Raised to collapse/restore the sidebar (the view owns the grid column).
    public event Action? SidebarToggleRequested;
    // Raised to open the "Open SQL file" picker; MainWindow owns the
    // StorageProvider dialog and file I/O.
    public event Action? OpenFileRequested;
    // Raised to save the active tab's SQL to disk; true = "save as" (always
    // prompt), false = save-in-place (prompt only when the tab has no file yet).
    public event Action<bool>? SaveFileRequested;
    // Raised to open a specific recent file (from the palette's "Recent file" entries).
    public event Action<string>? OpenRecentFileRequested;
    // Raised to comment/uncomment the selected lines; the editor panel owns the
    // document, same split as Format SQL.
    public event Action? ToggleLineCommentRequested;

    // Every catalog command resolves to an ICommand here (see CommandBindings),
    // so the view-only actions above get thin command wrappers rather than the
    // palette and the key bindings each raising the event their own way.
    [RelayCommand]
    private void ImportPlan() => ImportPlanRequested?.Invoke();

    [RelayCommand]
    private void ExpandStar() => ExpandStarRequested?.Invoke();

    [RelayCommand]
    private void Find() => FindRequested?.Invoke(false);

    [RelayCommand]
    private void FindReplace() => FindRequested?.Invoke(true);

    [RelayCommand]
    private void ToggleLineComment() => ToggleLineCommentRequested?.Invoke();

    [RelayCommand]
    private void ToggleSidebar() => SidebarToggleRequested?.Invoke();

    [RelayCommand]
    private void ToggleTheme() => ThemeToggleRequested?.Invoke();

    // --- The shell's three dismissable panels ---------------------------------
    //
    // Shortcuts, preferences and About are OverlayPanels over this window rather than
    // windows of their own (Nimbus.Ui.Controls.OverlayPanel — read that file for why).
    // So they are bindable state here, not the "raise an event, let the view open a
    // Window" shape the rest of this file uses: there is nothing left for the view to
    // own. Each IsOpen binds two-way and the panel closes itself; never pair one with a
    // closing command, which is the double-toggle no-op of DESIGN.md rule 6.

    [ObservableProperty]
    private bool _isShortcutsOpen;

    [ObservableProperty]
    private bool _isPreferencesOpen;

    [ObservableProperty]
    private bool _isAboutOpen;

    /// <summary>
    /// The cheat sheet's rows. Rebuilt on each open rather than held, because the key
    /// caps spell out Ctrl or Cmd: a sheet built once would keep showing the other
    /// platform's chords after the hotkey preference changed.
    /// </summary>
    [ObservableProperty]
    private ShortcutsViewModel? _shortcuts;

    /// <summary>The preferences page's own view model; built on open, detached on close.</summary>
    [ObservableProperty]
    private PreferencesViewModel? _preferences;

    /// <summary>F1 and the ? button both toggle, so the key that opens it also closes it.</summary>
    [RelayCommand]
    private void ShowShortcuts() => IsShortcutsOpen = !IsShortcutsOpen;

    partial void OnIsShortcutsOpenChanged(bool value) =>
        Shortcuts = value ? new ShortcutsViewModel() : null;

    /// <summary>
    /// The page subscribes to this view model to mirror the settings the shell owns,
    /// so an open page is a live listener and closing one has to
    /// <see cref="PreferencesViewModel.Detach"/> — otherwise every dismissed page stays
    /// subscribed for the life of the window.
    /// </summary>
    partial void OnIsPreferencesOpenChanged(bool value)
    {
        if (value)
        {
            Preferences ??= new PreferencesViewModel(this);
            return;
        }

        Preferences?.Detach();
        Preferences = null;
    }

    /// <summary>Opens the About box. Reached from the ☰ menu and the macOS app menu.</summary>
    [RelayCommand]
    private void ShowAbout() => IsAboutOpen = true;

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
    private void ShowPreferences() => IsPreferencesOpen = true;

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

    [RelayCommand]
    private void ShowSecurity() => SecurityRequested?.Invoke();

    /// <summary>
    /// The schema tree's route into the Roles &amp; Permissions window, on a
    /// named role when the user right-clicked one. The name is parked on the
    /// view model rather than passed to the window, because the window may be
    /// opening for the first time (its snapshot has not been read yet) or may
    /// already be up (nothing will refresh) - so both paths have to pick it up,
    /// and ApplyPendingRoleSelection covers the second.
    /// </summary>
    private Task ManageRolesAsync(string? role)
    {
        Security.PendingRoleSelection = role;
        SecurityRequested?.Invoke();
        Security.ApplyPendingRoleSelection();
        return Task.CompletedTask;
    }

    // Relations rarely change mid-session, so the palette's "jump to a table"
    // list — and the sidebar filter, which matches the same list so a table in a
    // collapsed schema is still findable — is fetched once and reused. Cleared by
    // RefreshSchemaAsync.
    private IReadOnlyList<RelationInfo>? _relationCache;

    /// <summary>Every relation in the database, fetched once and shared by the command palette and the schema tree's filter box.</summary>
    private async Task<IReadOnlyList<RelationInfo>> GetRelationsAsync() =>
        _relationCache ??= await _schemaService.GetAllRelationsAsync(CancellationToken.None);

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

    // Schemas the editor's completion ignores, for this connection. Ordinal:
    // Postgres identifiers are case-sensitive as stored. Handed to the
    // completion provider by reference, so the provider sees every change
    // without being re-wired; the *effect* still lands on its next refresh.
    private readonly HashSet<string> _excludedSchemas;

    private readonly Action<IReadOnlyList<string>>? _persistExcludedSchemas;

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
        RoleService roleService,
        PrivilegeService privilegeService,
        SecurityEditor securityEditor,
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
        Action<IReadOnlyList<string>>? persistRecentSqlFiles = null,
        IEnumerable<string>? excludedSchemas = null,
        Action<IReadOnlyList<string>>? persistExcludedSchemas = null)
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
        _excludedSchemas = new HashSet<string>(excludedSchemas ?? [], StringComparer.Ordinal);
        _persistExcludedSchemas = persistExcludedSchemas;
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
        SchemaTree.NewTableRequested = NewTableAsync;
        SchemaTree.DropSchemaRequested = DropSchemaAsync;
        SchemaTree.SetSchemaExcludedFromCompletionRequested = SetSchemaExcludedFromCompletionAsync;
        SchemaTree.ManageRolesRequested = ManageRolesAsync;
        SchemaTree.IsSchemaExcludedFromCompletion = _excludedSchemas.Contains;
        SchemaTree.AllRelationsRequested = GetRelationsAsync;
        _schemaService = schemaService;
        _schemaEditor = schemaEditor;
        _ddlService = ddlService;
        CompletionProvider = completionProvider;
        // Same set object the toggle mutates, so the provider never holds a
        // stale copy; it reads it on each refresh.
        CompletionProvider.ExcludedSchemas = _excludedSchemas;
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
        Security = new SecurityViewModel(roleService, privilegeService, securityEditor, connectionDatabase);
        // Every privilege change leaves the security window as a script in a new
        // editor tab rather than being applied from there - see OpenGeneratedSql.
        Security.OpenSqlInNewTab = (title, sql) => OpenGeneratedSql(title, sql);
        Security.RolesChanged = SchemaTree.RefreshRolesAsync;
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
                // A persisted title the SQL would produce on its own carries no
                // information, and pinning it as an override would freeze the
                // tab's name against later edits — snapshots written before
                // labels and overrides were separated store browse labels here.
                // Dropping it displays exactly the same name, automatically.
                tab.TitleOverride = string.Equals(saved.Title, tab.TabTitle, StringComparison.Ordinal)
                    ? null
                    : saved.Title;

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

    // Always available: closing the only tab replaces it with a fresh empty one
    // (see CloseTab) rather than being refused.
    private bool CanCloseTab() => Tabs.Count > 0;

    [RelayCommand]
    private void AddTab() => NewTab();

    // Creates a query tab, wires its history hook, and makes it active.
    private QueryViewModel NewTab()
    {
        var tab = new QueryViewModel(_engine, _explainService, GetReconcilerAsync, () => SafeModeEdits, _schemaService) { DefaultTitle = $"Query {Tabs.Count + 1}" };
        tab.Executed += SavedQueries.RecordExecution;
        Tabs.Add(tab);
        ActiveTab = tab;
        NotifyTabCommands();
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
        // "Close tabs to the right" is position-dependent — a reorder can flip
        // it either way for the tab that just moved.
        NotifyTabCommands();
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
        tab.DefaultTitle = $"{table.Name} · source";
        tab.Sql = ddl;
    }

    /// <summary>Opens pg_get_functiondef's stored definition of a function/procedure in a new tab.</summary>
    public async Task ShowFunctionSourceAsync(FunctionNode function)
    {
        var ddl = await _ddlService.GenerateFunctionAsync(function.Schema, function.Name, function.Arguments, CancellationToken.None);

        var tab = NewTab();
        tab.DefaultTitle = $"{function.Name} · source";
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
        tab.DefaultTitle = "Imported plan";
        tab.ShowImportedPlan(plan.Result, plan.DisplayText, plan.RawJson);
    }

    /// <summary>
    /// Drops a generated script into a new tab, where it can be read, edited and
    /// run. This is how every privilege change leaves the Roles &amp; Permissions
    /// window: the window composes the GRANT/REVOKE text, the user reviews it in
    /// the editor they already trust, and nothing is applied behind their back.
    /// The one deliberate exception is a statement carrying a password, which
    /// never comes through here — see <c>SecurityEditor</c>.
    /// </summary>
    public QueryViewModel OpenGeneratedSql(string title, string sql)
    {
        var tab = NewTab();
        tab.DefaultTitle = title;
        tab.Sql = sql;
        return tab;
    }

    /// <summary>
    /// The schema context menu's "New table…": drops a CREATE TABLE skeleton for
    /// the schema into a new tab (never the active one, per the "loading never
    /// overwrites" rule), where it can be edited and run. Deliberately a
    /// template rather than a dialog — see <see cref="DdlTemplates"/>.
    /// </summary>
    public Task NewTableAsync(SchemaNode schema)
    {
        var tab = NewTab();
        tab.DefaultTitle = $"{schema.Name} · new table";
        tab.Sql = DdlTemplates.NewTable(schema.Name);
        return Task.CompletedTask;
    }

    /// <summary>
    /// DROP SCHEMA (the view confirms first; <paramref name="cascade"/> is its
    /// separately confirmed variant), then reload everything derived from the
    /// catalog — the schema is gone from the tree, the autocomplete and the
    /// palette in one pass. Errors land in the sidebar's message strip, which is
    /// where a plain RESTRICT refusal ("schema is not empty") shows up too.
    /// </summary>
    public async Task DropSchemaAsync(SchemaNode schema, bool cascade)
    {
        SchemaTree.ErrorMessage = null;
        try
        {
            await _schemaEditor.DropSchemaAsync(schema.Name, cascade, CancellationToken.None);
            await RefreshSchemaAsync();
        }
        catch (Exception ex)
        {
            SchemaTree.ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Adds/removes a schema from the completion exclusion set, persists it for
    /// this connection, and rebuilds the completion cache so the change is live
    /// in the editor immediately. The tree keeps the schema either way (dimmed
    /// when excluded) — nothing here touches what the sidebar shows.
    /// </summary>
    public async Task SetSchemaExcludedFromCompletionAsync(SchemaNode schema, bool excluded)
    {
        if (excluded)
        {
            _excludedSchemas.Add(schema.Name);
        }
        else
        {
            _excludedSchemas.Remove(schema.Name);
        }

        schema.ExcludedFromCompletion = excluded;
        _persistExcludedSchemas?.Invoke(_excludedSchemas.ToList());
        ActiveTab.Status = excluded
            ? $"Schema \"{schema.Name}\" excluded from autocomplete"
            : $"Schema \"{schema.Name}\" back in autocomplete";

        // Only the completion cache is derived from the exclusion set; the tree
        // and the palette deliberately still show everything, so a full
        // RefreshSchemaAsync would collapse the tree for nothing.
        await CompletionProvider.RefreshAsync(CancellationToken.None);
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

    /// <summary>
    /// Closes <paramref name="tab"/> (the active one when invoked with no
    /// parameter). Closing the *last* tab empties it rather than refusing:
    /// a fresh scratch tab takes its place, Notepad++-style, so "close" always
    /// does something and the window is never left tab-less (every binding
    /// under <c>ActiveTab</c> depends on there being one).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private void CloseTab(QueryViewModel? tab)
    {
        tab ??= ActiveTab;
        if (Tabs.Count == 1)
        {
            if (!ReferenceEquals(Tabs[0], tab))
            {
                return;
            }

            // The replacement is created *before* the removal so the strip is
            // never momentarily empty — same reason the successor below is
            // selected first. It is about to be the only tab, so it is "Query 1"
            // however many tabs this session has been through.
            NewTab().DefaultTitle = "Query 1";
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // Move the selection off the doomed tab *before* removing it. Removing
        // the ListBox's selected item makes its two-way SelectedItem binding
        // push ActiveTab = null synchronously, and every binding under
        // ActiveTab logs a path error against that transient null (a screenful
        // of [Binding] noise per closed tab) before a post-removal reassignment
        // could put it right. Reselecting first means the removal never touches
        // the selection at all. Successor: the tab to the right, or the one to
        // the left when the last tab is closing — the count guard above
        // guarantees a neighbour exists.
        if (ActiveTab is null || ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs[index < Tabs.Count - 1 ? index + 1 : index - 1];
        }

        // A query still running in the closed tab would otherwise keep streaming
        // in the background, holding a pool connection and server-side work for a
        // result nothing will show — cancel it as the tab goes away.
        tab.CancelCommand.Execute(null);

        tab.Executed -= SavedQueries.RecordExecution;
        Tabs.RemoveAt(index);

        NotifyTabCommands();
    }

    /// <summary>
    /// Opens the inline rename box on <paramref name="tab"/> — the right-clicked
    /// tab from the strip's menu, the active one from the palette. The name a
    /// user types wins over the automatic (SQL-derived) one from then on and
    /// rides the workspace snapshot into the next session; clearing it hands the
    /// tab back to automatic naming.
    /// </summary>
    [RelayCommand]
    private void RenameTab(QueryViewModel? tab) => (tab ?? ActiveTab)?.BeginRename();

    private bool CanCloseOtherTabs(QueryViewModel? tab) => Tabs.Count > 1 && Tabs.Contains(tab ?? ActiveTab);

    /// <summary>
    /// Closes every tab except <paramref name="tab"/> (the active one when the
    /// palette invokes this with no parameter). Goes through <see cref="CloseTab"/>
    /// per tab so each one still cancels its running query and unhooks its
    /// history handler; the snapshot is taken first because that mutates
    /// <see cref="Tabs"/> underneath the enumeration.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseOtherTabs))]
    private void CloseOtherTabs(QueryViewModel? tab)
    {
        tab ??= ActiveTab;
        foreach (var other in Tabs.Where(t => !ReferenceEquals(t, tab)).ToList())
        {
            CloseTab(other);
        }
    }

    private bool CanCloseTabsToTheRight(QueryViewModel? tab)
    {
        var index = Tabs.IndexOf(tab ?? ActiveTab);
        return index >= 0 && index < Tabs.Count - 1;
    }

    /// <summary>Closes everything after <paramref name="tab"/> in strip order.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseTabsToTheRight))]
    private void CloseTabsToTheRight(QueryViewModel? tab)
    {
        tab ??= ActiveTab;
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        foreach (var right in Tabs.Skip(index + 1).ToList())
        {
            CloseTab(right);
        }
    }

    // Every close command's CanExecute reads the tab count or a tab's position,
    // so all three re-evaluate together whenever the strip's contents or order
    // change — one call site instead of three easy-to-forget ones.
    private void NotifyTabCommands()
    {
        CloseTabCommand.NotifyCanExecuteChanged();
        CloseOtherTabsCommand.NotifyCanExecuteChanged();
        CloseTabsToTheRightCommand.NotifyCanExecuteChanged();
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

    /// <summary>
    /// Ctrl/Cmd+1…9: activates the nth tab (0-based). Browser convention —
    /// a number past the end of the strip does nothing rather than wrapping.
    /// </summary>
    [RelayCommand]
    private void GoToTab(int index)
    {
        if (index >= 0 && index < Tabs.Count)
        {
            ActiveTab = Tabs[index];
        }
    }

    public Task PreviewTableAsync(TableNode table) => PreviewTableAsync(table.Schema, table.Name);

    public async Task PreviewTableAsync(string schema, string name, string? initialFilter = null)
    {
        // Opens in a new tab rather than the active one - see the "loading a
        // query never overwrites the active tab" rule (CLAUDE.md).
        // A label, not an override: the tab is named after the browsed table
        // only until its SQL says otherwise — retyping the query in this tab
        // renames it after what it now selects from.
        var tab = NewTab();
        tab.DefaultTitle = name;

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
            _relationCache = await GetRelationsAsync();
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

    // One row per catalog entry flagged for the palette, in catalog order —
    // title, glyph and the trailing shortcut label all come from there, so the
    // palette can't drift from the key bindings or the F1 sheet.
    private IEnumerable<PaletteItem> BuildActionItems() =>
        CommandCatalog.On(CommandSurface.Palette).Select(descriptor => new PaletteItem(
            descriptor.Title,
            "Action",
            descriptor.Glyph,
            Invoke(() => CommandBindings.Resolve(descriptor.Id, this)),
            descriptor.ShortcutLabel(Hotkeys.CommandLabel)));

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
