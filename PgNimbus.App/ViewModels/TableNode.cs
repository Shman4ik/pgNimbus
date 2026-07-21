using PgNimbus.Core;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class TableNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;
    private readonly Func<bool> _showSizes;
    private readonly Func<bool> _showAdvanced;

    public TableNode(
        SchemaService schemaService, string schema, string name, RelationKind kind,
        long? totalBytes = null, Func<bool>? showSizes = null, Func<bool>? showAdvanced = null)
    {
        _schemaService = schemaService;
        Schema = schema;
        Name = name;
        Kind = kind;
        TotalBytes = totalBytes;
        _showSizes = showSizes ?? (static () => false);
        _showAdvanced = showAdvanced ?? (static () => false);
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

    /// <summary>Only relations with real storage carry indexes (tables, matviews, partitioned parents) — not plain views.</summary>
    private bool CanHaveIndexes => Kind is RelationKind.Table or RelationKind.MaterializedView or RelationKind.PartitionedTable;

    /// <summary>Triggers can sit on tables, (INSTEAD OF) views, and partitioned parents — but not on materialized views.</summary>
    private bool CanHaveTriggers => Kind is RelationKind.Table or RelationKind.View or RelationKind.PartitionedTable;

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var columns = await _schemaService.GetColumnsAsync(Schema, Name, CancellationToken.None);
        var children = columns.Select(c => (SchemaTreeNode)new ColumnNode(c)).ToList();
        if (_showAdvanced())
        {
            children.AddRange(AdvancedSubGroups());
        }

        return children;
    }

    private IEnumerable<SchemaTreeNode> AdvancedSubGroups()
    {
        if (CanHaveIndexes)
        {
            yield return new IndexesGroupNode(_schemaService, Schema, Name);
        }

        if (CanHaveTriggers)
        {
            yield return new TriggersGroupNode(_schemaService, Schema, Name);
        }
    }

    /// <summary>
    /// Adds/removes this table's Indexes/Triggers sub-groups in place when the
    /// advanced-objects toggle flips. Only touches an already-loaded (column) child
    /// list — an unexpanded table picks the current toggle state up on first expand.
    /// </summary>
    public void SetAdvancedSubGroupsVisible(bool visible)
    {
        if (Children.Any(c => c is PlaceholderNode or ErrorNode))
        {
            return;
        }

        var hasGroups = Children.Any(c => c is IndexesGroupNode or TriggersGroupNode);
        if (visible && !hasGroups)
        {
            foreach (var group in AdvancedSubGroups())
            {
                Children.Add(group);
            }
        }
        else if (!visible && hasGroups)
        {
            foreach (var group in Children.Where(c => c is IndexesGroupNode or TriggersGroupNode).ToList())
            {
                Children.Remove(group);
            }
        }
    }
}
