using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.App.Completion;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

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

    public CommandPaletteViewModel CommandPalette { get; } = new();

    public CellInspectorViewModel CellInspector { get; } = new();

    // Palette actions that need the window (theme, dialogs) live in the view;
    // MainWindow subscribes to these so the palette can trigger them.
    public event Action? ThemeToggleRequested;
    public event Action? ShortcutsRequested;
    // Raised when the user asks to connect to a different server/database;
    // MainWindow reopens the connection dialog (App.BuildConnectionDialog).
    public event Action? SwitchConnectionRequested;
    // Raised to pretty-print the statement under the caret; MainWindow owns the
    // editor text (AvaloniaEdit's Text isn't bindable) so it does the rewrite.
    public event Action? FormatSqlRequested;

    [RelayCommand]
    private void SwitchConnection() => SwitchConnectionRequested?.Invoke();

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

    public MainViewModel(
        QueryEngine engine,
        ExplainService explainService,
        SchemaTreeViewModel schemaTree,
        SchemaService schemaService,
        SchemaEditor schemaEditor,
        DdlService ddlService,
        SqlCompletionProvider completionProvider,
        NotifyMonitorViewModel notifyMonitor,
        string? accentColor = null,
        string connectionHost = "",
        string connectionDatabase = "")
    {
        ConnectionHost = connectionHost;
        ConnectionDatabase = connectionDatabase;
        _engine = engine;
        _explainService = explainService;
        SchemaTree = schemaTree;
        _schemaService = schemaService;
        _schemaEditor = schemaEditor;
        _ddlService = ddlService;
        CompletionProvider = completionProvider;
        SavedQueries = new SavedQueriesViewModel(
            new SavedQueryStore(),
            new QueryHistoryStore(),
            () => ActiveTab,
            // History entries are stamped with this label for per-connection scoping.
            () => string.IsNullOrEmpty(ConnectionHost) ? null : $"{ConnectionHost}/{ConnectionDatabase}");
        NotifyMonitor = notifyMonitor;
        AccentColor = accentColor;

        // The engine owns the transaction state; mirror it here so the indicator
        // and command availability follow every change — including an
        // auto-rollback that fires from a background query thread.
        _engine.TransactionStateChanged += OnEngineTransactionStateChanged;

        AddTab();
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

    public AddRowViewModel CreateAddRowViewModel(string schema, string table) =>
        new(_engine, _schemaService, schema, table);

    /// <summary>
    /// Reloads everything derived from the live catalog — the schema tree, the
    /// autocomplete cache, and the command palette's table list — so objects
    /// created or altered in another session (or via a DDL statement here) show
    /// up without reconnecting.
    /// </summary>
    [RelayCommand]
    private async Task RefreshSchemaAsync()
    {
        // Force the palette to re-fetch relations, and the fix reconciler to
        // rebuild its name snapshot, on next use.
        _relationCache = null;
        _reconciler = null;

        await Task.WhenAll(
            SchemaTree.RefreshCommand.ExecuteAsync(null),
            CompletionProvider.RefreshAsync(CancellationToken.None));
    }

    private bool CanCloseTab() => Tabs.Count > 1;

    [RelayCommand]
    private void AddTab() => NewTab();

    // Creates a query tab, wires its history hook, and makes it active.
    private QueryViewModel NewTab()
    {
        var tab = new QueryViewModel(_engine, _explainService, GetReconcilerAsync) { DefaultTitle = $"Query {Tabs.Count + 1}" };
        tab.Executed += SavedQueries.RecordExecution;
        Tabs.Add(tab);
        ActiveTab = tab;
        CloseTabCommand.NotifyCanExecuteChanged();
        return tab;
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

    public async Task PreviewTableAsync(string schema, string name)
    {
        var columns = await _schemaService.GetColumnsAsync(schema, name, CancellationToken.None);
        var primaryKeyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        // Open the table in no-SQL browse mode: server-side filter/sort/paging,
        // with inline editing when the table has a primary key.
        await ActiveTab.StartBrowseAsync(schema, name, primaryKeyColumns);
    }

    /// <summary>
    /// Opens the command palette. Actions and saved queries are available
    /// instantly; the (potentially larger) table list is fetched in the
    /// background and merged in without blocking the palette from showing.
    /// </summary>
    public async Task OpenCommandPaletteAsync()
    {
        var baseItems = BuildActionItems().Concat(BuildSavedQueryItems()).ToList();
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
        yield return new PaletteItem("Run query", "Action", "▶", Invoke(() => ActiveTab.RunCommand));
        yield return new PaletteItem("Cancel query", "Action", "■", Invoke(() => ActiveTab.CancelCommand));
        yield return new PaletteItem("Explain", "Action", "⚡", Invoke(() => ActiveTab.ExplainCommand));
        yield return new PaletteItem("Explain Analyze", "Action", "⚡", Invoke(() => ActiveTab.ExplainAnalyzeCommand));
        yield return new PaletteItem("Begin transaction", "Action", "⛃", Invoke(() => BeginTransactionCommand));
        yield return new PaletteItem("Commit transaction", "Action", "✓", Invoke(() => CommitTransactionCommand));
        yield return new PaletteItem("Rollback transaction", "Action", "↺", Invoke(() => RollbackTransactionCommand));
        yield return new PaletteItem("Refresh database & schema", "Action", "⟳", Invoke(() => RefreshSchemaCommand));
        yield return new PaletteItem("New query tab", "Action", "＋", Invoke(() => AddTabCommand));
        yield return new PaletteItem("Close tab", "Action", "✕", Invoke(() => CloseTabCommand));
        yield return new PaletteItem("Next tab", "Action", "›", Invoke(() => NextTabCommand));
        yield return new PaletteItem("Previous tab", "Action", "‹", Invoke(() => PreviousTabCommand));
        yield return new PaletteItem("Format SQL", "Action", "❖", () => { FormatSqlRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Switch connection…", "Action", "⇄", () => { SwitchConnectionRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Toggle light/dark theme", "Action", "◐", () => { ThemeToggleRequested?.Invoke(); return Task.CompletedTask; });
        yield return new PaletteItem("Keyboard shortcuts", "Action", "?", () => { ShortcutsRequested?.Invoke(); return Task.CompletedTask; });
    }

    private IEnumerable<PaletteItem> BuildSavedQueryItems() =>
        SavedQueries.SavedQueries.Select(q => new PaletteItem(
            q.Name,
            "Saved query",
            "★",
            () => { SavedQueries.LoadSavedQueryCommand.Execute(q); return Task.CompletedTask; }));

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
