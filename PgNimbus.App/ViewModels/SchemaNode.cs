using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class SchemaNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public SchemaNode(SchemaService schemaService, string name)
    {
        _schemaService = schemaService;
        Name = name;
        MarkExpandable();
    }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var tables = await _schemaService.GetTablesAsync(Name, CancellationToken.None);
        var children = tables.Select(t => (SchemaTreeNode)new TableNode(_schemaService, Name, t.Name, t.Kind)).ToList();
        // Functions live in a sub-group so a schema with many of them doesn't drown its tables.
        children.Add(new FunctionsGroupNode(_schemaService, Name));
        return children;
    }
}
