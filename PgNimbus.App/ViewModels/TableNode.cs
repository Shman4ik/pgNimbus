using PgNimbus.Core;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class TableNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public TableNode(SchemaService schemaService, string schema, string name, RelationKind kind, long? totalBytes = null)
    {
        _schemaService = schemaService;
        Schema = schema;
        Name = name;
        Kind = kind;
        TotalBytes = totalBytes;
        MarkExpandable();
    }

    public string Schema { get; }

    public RelationKind Kind { get; }

    /// <summary>Total on-disk size (heap + indexes + TOAST), or null for views/partitioned parents.</summary>
    public long? TotalBytes { get; }

    /// <summary>The dim, right-aligned size hint in the tree — empty when there's no size to show.</summary>
    public string SizeText => TotalBytes is { } bytes ? ByteSize.Format(bytes) : "";

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
