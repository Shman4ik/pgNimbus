using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class TableNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public TableNode(SchemaService schemaService, string schema, string name, RelationKind kind)
    {
        _schemaService = schemaService;
        Schema = schema;
        Name = name;
        Kind = kind;
        MarkExpandable();
    }

    public string Schema { get; }

    public RelationKind Kind { get; }

    public string Glyph => Kind switch
    {
        RelationKind.Table => "▤",
        RelationKind.View => "▥",
        RelationKind.MaterializedView => "▦",
        RelationKind.PartitionedTable => "▧",
        _ => "▤",
    };

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var columns = await _schemaService.GetColumnsAsync(Schema, Name, CancellationToken.None);
        return columns.Select(c => (SchemaTreeNode)new ColumnNode(c)).ToList();
    }
}
