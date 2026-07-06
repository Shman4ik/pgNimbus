using System.Collections.ObjectModel;
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

    public SchemaTreeViewModel SchemaTree { get; }

    public SqlCompletionProvider CompletionProvider { get; }

    public SavedQueriesViewModel SavedQueries { get; }

    public NotifyMonitorViewModel NotifyMonitor { get; }

    public CommandPaletteViewModel CommandPalette { get; } = new();

    // Palette actions that need the window (theme, dialogs) live in the view;
    // MainWindow subscribes to these so the palette can trigger them.
    public event Action? ThemeToggleRequested;
    public event Action? ShortcutsRequested;

    // Relations rarely change mid-session, so the palette's "jump to a table"
    // list is fetched once and reused across opens.
    private IReadOnlyList<RelationInfo>? _relationCache;

    /// <summary>The connected profile's accent color ("#RRGGBB"), or null. Lets the window chrome show at a glance which environment (e.g. prod vs. dev) is connected.</summary>
    public string? AccentColor { get; }

    /// <summary>Server host for the title-bar breadcrumb (host › database).</summary>
    public string ConnectionHost { get; }

    /// <summary>Database name for the title-bar breadcrumb (host › database).</summary>
    public string ConnectionDatabase { get; }

    public ObservableCollection<QueryViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private QueryViewModel _activeTab = null!;

    public MainViewModel(
        QueryEngine engine,
        ExplainService explainService,
        SchemaTreeViewModel schemaTree,
        SchemaService schemaService,
        SchemaEditor schemaEditor,
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
        CompletionProvider = completionProvider;
        SavedQueries = new SavedQueriesViewModel(new SavedQueryStore(), new QueryHistoryStore(), () => ActiveTab);
        NotifyMonitor = notifyMonitor;
        AccentColor = accentColor;

        AddTab();
    }

    public AlterTableViewModel CreateAlterTableViewModel(TableNode table) =>
        new(_schemaEditor, _schemaService, table.Schema, table.Name);

    private bool CanCloseTab() => Tabs.Count > 1;

    [RelayCommand]
    private void AddTab()
    {
        var tab = new QueryViewModel(_engine, _explainService) { DefaultTitle = $"Query {Tabs.Count + 1}" };
        tab.Executed += SavedQueries.RecordExecution;
        Tabs.Add(tab);
        ActiveTab = tab;
        CloseTabCommand.NotifyCanExecuteChanged();
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
        ActiveTab.Sql = $"SELECT * FROM {SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(name)} LIMIT 100;";

        var columns = await _schemaService.GetColumnsAsync(schema, name, CancellationToken.None);
        var primaryKeyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        if (primaryKeyColumns.Count > 0)
        {
            ActiveTab.EditContext = new EditableTableContext(schema, name, primaryKeyColumns);
        }
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
        yield return new PaletteItem("New query tab", "Action", "＋", Invoke(() => AddTabCommand));
        yield return new PaletteItem("Close tab", "Action", "✕", Invoke(() => CloseTabCommand));
        yield return new PaletteItem("Next tab", "Action", "›", Invoke(() => NextTabCommand));
        yield return new PaletteItem("Previous tab", "Action", "‹", Invoke(() => PreviousTabCommand));
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
