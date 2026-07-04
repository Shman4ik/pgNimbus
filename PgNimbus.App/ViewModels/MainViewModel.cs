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

    public SchemaTreeViewModel SchemaTree { get; }

    public SqlCompletionProvider CompletionProvider { get; }

    public SavedQueriesViewModel SavedQueries { get; }

    public NotifyMonitorViewModel NotifyMonitor { get; }

    public ObservableCollection<QueryViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private QueryViewModel _activeTab = null!;

    public MainViewModel(
        QueryEngine engine,
        ExplainService explainService,
        SchemaTreeViewModel schemaTree,
        SchemaService schemaService,
        SqlCompletionProvider completionProvider,
        NotifyMonitorViewModel notifyMonitor)
    {
        _engine = engine;
        _explainService = explainService;
        SchemaTree = schemaTree;
        _schemaService = schemaService;
        CompletionProvider = completionProvider;
        SavedQueries = new SavedQueriesViewModel(new SavedQueryStore(), new QueryHistoryStore(), () => ActiveTab);
        NotifyMonitor = notifyMonitor;

        AddTab();
    }

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

        tab.Executed -= SavedQueries.RecordExecution;
        Tabs.RemoveAt(index);

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        CloseTabCommand.NotifyCanExecuteChanged();
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
