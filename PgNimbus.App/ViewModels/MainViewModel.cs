namespace PgNimbus.App.ViewModels;

public sealed class MainViewModel
{
    public QueryViewModel Query { get; }

    public SchemaTreeViewModel SchemaTree { get; }

    public MainViewModel(QueryViewModel query, SchemaTreeViewModel schemaTree)
    {
        Query = query;
        SchemaTree = schemaTree;
    }

    public void PreviewTable(TableNode table)
    {
        Query.Sql = $"SELECT * FROM \"{table.Schema}\".\"{table.Name}\" LIMIT 100;";
    }
}
