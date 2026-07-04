using PgNimbus.App.Completion;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class MainViewModel
{
    private readonly SchemaService _schemaService;

    public QueryViewModel Query { get; }

    public SchemaTreeViewModel SchemaTree { get; }

    public SqlCompletionProvider CompletionProvider { get; }

    public SavedQueriesViewModel SavedQueries { get; }

    public MainViewModel(
        QueryViewModel query,
        SchemaTreeViewModel schemaTree,
        SchemaService schemaService,
        SqlCompletionProvider completionProvider,
        SavedQueriesViewModel savedQueries)
    {
        Query = query;
        SchemaTree = schemaTree;
        _schemaService = schemaService;
        CompletionProvider = completionProvider;
        SavedQueries = savedQueries;
    }

    public async Task PreviewTableAsync(TableNode table)
    {
        Query.Sql = $"SELECT * FROM {SqlIdentifier.Quote(table.Schema)}.{SqlIdentifier.Quote(table.Name)} LIMIT 100;";

        var columns = await _schemaService.GetColumnsAsync(table.Schema, table.Name, CancellationToken.None);
        var primaryKeyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        if (primaryKeyColumns.Count > 0)
        {
            Query.EditContext = new EditableTableContext(table.Schema, table.Name, primaryKeyColumns);
        }
    }
}
