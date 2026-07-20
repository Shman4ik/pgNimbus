using PgNimbus.Core;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class TableNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;
    private readonly Func<bool> _showSizes;

    public TableNode(SchemaService schemaService, string schema, string name, RelationKind kind, long? totalBytes = null, Func<bool>? showSizes = null)
    {
        _schemaService = schemaService;
        Schema = schema;
        Name = name;
        Kind = kind;
        TotalBytes = totalBytes;
        _showSizes = showSizes ?? (static () => false);
        MarkExpandable();
    }

    public string Schema { get; }

    public RelationKind Kind { get; }

    /// <summary>Total on-disk size (heap + indexes + TOAST), or null for views/partitioned parents.</summary>
    public long? TotalBytes { get; }

    /// <summary>
    /// The dim size hint next to the name — empty when the "show sizes" toggle is
    /// off or there's no size to show (views, partitioned parents). Off by default.
    /// </summary>
    public string SizeText => _showSizes() && TotalBytes is { } bytes ? ByteSize.Format(bytes) : "";

    /// <summary>Re-evaluate <see cref="SizeText"/> after the sidebar's "show sizes" toggle flips.</summary>
    public void NotifySizeVisibilityChanged() => OnPropertyChanged(nameof(SizeText));

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
