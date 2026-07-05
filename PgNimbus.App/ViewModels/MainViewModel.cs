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
        var tab = new QueryViewModel(_engine, _explainService) { TabTitle = $"Query {Tabs.Count + 1}" };
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

    public async Task PreviewTableAsync(TableNode table)
    {
        ActiveTab.Sql = $"SELECT * FROM {SqlIdentifier.Quote(table.Schema)}.{SqlIdentifier.Quote(table.Name)} LIMIT 100;";

        var columns = await _schemaService.GetColumnsAsync(table.Schema, table.Name, CancellationToken.None);
        var primaryKeyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        if (primaryKeyColumns.Count > 0)
        {
            ActiveTab.EditContext = new EditableTableContext(table.Schema, table.Name, primaryKeyColumns);
        }
    }
}
