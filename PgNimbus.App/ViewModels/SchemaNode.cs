using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class SchemaNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;
    private readonly Func<bool> _showFunctions;

    public SchemaNode(SchemaService schemaService, string name, Func<bool> showFunctions)
    {
        _schemaService = schemaService;
        _showFunctions = showFunctions;
        Name = name;
        MarkExpandable();
    }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var tables = await _schemaService.GetTablesAsync(Name, CancellationToken.None);
        var children = tables.Select(t => (SchemaTreeNode)new TableNode(_schemaService, Name, t.Name, t.Kind)).ToList();
        if (_showFunctions())
        {
            // Functions live in a sub-group so a schema with many of them doesn't drown its tables.
            children.Add(new FunctionsGroupNode(_schemaService, Name));
        }

        return children;
    }

    /// <summary>
    /// Adds/removes the "Functions" sub-group in place when the advanced-objects
    /// toggle flips. Only touches an already-loaded child list — an unloaded
    /// schema picks the current toggle state up on first expand.
    /// </summary>
    public void SetFunctionsGroupVisible(bool visible)
    {
        var existing = Children.OfType<FunctionsGroupNode>().FirstOrDefault();
        if (visible == (existing is not null))
        {
            return;
        }

        if (!visible)
        {
            Children.Remove(existing!);
        }
        else if (!Children.Any(c => c is PlaceholderNode or ErrorNode))
        {
            Children.Add(new FunctionsGroupNode(_schemaService, Name));
        }
    }
}
