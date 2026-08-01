using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed partial class SchemaNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;
    private readonly Func<bool> _showAdvanced;
    private readonly Func<bool> _showSizes;

    /// <summary>
    /// Whether the editor's completion ignores this schema (the context menu's
    /// "Exclude from autocomplete"). The node stays in the tree either way —
    /// dimmed, with a marker — so an exclusion is visible where it was made and
    /// one right-click away from being undone. Persisted per connection by the
    /// host; see <see cref="PgNimbus.Core.Settings.AutocompleteExclusions"/>.
    /// </summary>
    [ObservableProperty]
    private bool _excludedFromCompletion;

    public SchemaNode(SchemaService schemaService, string name, Func<bool> showAdvanced, Func<bool> showSizes, bool excludedFromCompletion = false)
    {
        _schemaService = schemaService;
        _showAdvanced = showAdvanced;
        _showSizes = showSizes;
        _excludedFromCompletion = excludedFromCompletion;
        Name = name;
        MarkExpandable();
    }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var tables = await _schemaService.GetTablesAsync(Name, CancellationToken.None);
        var children = tables
            .Select(t => (SchemaTreeNode)new TableNode(_schemaService, Name, t.Name, t.Kind, t.TotalBytes, _showSizes, _showAdvanced))
            .ToList();
        if (_showAdvanced())
        {
            // The advanced schema objects live in their own sub-groups so a schema
            // with many of them doesn't drown its tables.
            children.AddRange(AdvancedGroups());
        }

        return children;
    }

    private IEnumerable<SchemaTreeNode> AdvancedGroups()
    {
        yield return new FunctionsGroupNode(_schemaService, Name);
        yield return new SequencesGroupNode(_schemaService, Name);
        yield return new TypesGroupNode(_schemaService, Name);
    }

    /// <summary>
    /// Adds/removes the advanced sub-groups (Functions, Sequences, Types) in place
    /// when the advanced-objects toggle flips, and cascades the same flip to each
    /// loaded table's own Indexes/Triggers sub-groups. Only touches an already-loaded
    /// child list — an unloaded schema picks the current toggle state up on first expand.
    /// </summary>
    public void SetAdvancedGroupsVisible(bool visible)
    {
        // A not-yet-loaded (placeholder) or errored schema has no real children to flip.
        if (Children.Any(c => c is PlaceholderNode or ErrorNode))
        {
            return;
        }

        var hasGroups = Children.Any(c => c is FunctionsGroupNode);
        if (visible && !hasGroups)
        {
            foreach (var group in AdvancedGroups())
            {
                Children.Add(group);
            }
        }
        else if (!visible && hasGroups)
        {
            foreach (var group in Children.Where(IsAdvancedGroup).ToList())
            {
                Children.Remove(group);
            }
        }

        foreach (var table in Children.OfType<TableNode>())
        {
            table.SetAdvancedSubGroupsVisible(visible);
        }
    }

    private static bool IsAdvancedGroup(SchemaTreeNode node) =>
        node is FunctionsGroupNode or SequencesGroupNode or TypesGroupNode;
}
