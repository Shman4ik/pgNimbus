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
        return tables.Select(t => (SchemaTreeNode)new TableNode(_schemaService, Name, t.Name, t.Kind)).ToList();
    }
}
