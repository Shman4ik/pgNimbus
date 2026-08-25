using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Base node for the schema sidebar tree. Children load lazily on first
/// expand so opening a connection doesn't eagerly walk the whole catalog.
/// </summary>
public abstract partial class SchemaTreeNode : ObservableObject
{
    private bool _loaded;

    /// <summary>
    /// True once this node's children have actually been fetched. Lets a caller
    /// refresh a node it knows the user has opened without forcing a catalog
    /// read for one they never expanded.
    /// </summary>
    public bool IsLoaded => _loaded;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Whether this node passes the sidebar filter. Bound to <c>TreeViewItem.IsVisible</c>; true when no filter is active.</summary>
    [ObservableProperty]
    private bool _isFilteredIn = true;

    public string Name { get; init; } = string.Empty;

    public ObservableCollection<SchemaTreeNode> Children { get; } = [];

    /// <summary>Seeds a placeholder child so an as-yet-unloaded expandable node still shows an expand arrow.</summary>
    protected void MarkExpandable() => Children.Add(new PlaceholderNode());

    /// <summary>Re-fetches this node's children immediately, bypassing the lazy-load-once gate (used after schema-changing operations like ALTER TABLE).</summary>
    public Task RefreshAsync() => LoadChildrenAsync();

    /// <summary>
    /// Fills this node's children in up front and marks it loaded, so expanding
    /// it never reaches for the catalog. The headless screenshot harness
    /// (tools/Screenshot) builds its fixture trees this way; production always
    /// loads lazily through <see cref="FetchChildrenAsync"/>.
    /// </summary>
    public void SeedChildren(IEnumerable<SchemaTreeNode> children)
    {
        Children.Clear();
        foreach (var child in children)
        {
            Children.Add(child);
        }

        _loaded = true;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded)
        {
            return;
        }

        _loaded = true;
        _ = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        IsLoading = true;
        try
        {
            var children = await FetchChildrenAsync();
            Children.Clear();
            foreach (var child in children)
            {
                Children.Add(child);
            }
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new ErrorNode { Name = ex.Message });
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected virtual Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync() =>
        Task.FromResult<IReadOnlyList<SchemaTreeNode>>([]);
}

/// <summary>Placeholder child so a lazily-loaded node still shows an expand arrow.</summary>
public sealed class PlaceholderNode : SchemaTreeNode;

public sealed class ErrorNode : SchemaTreeNode;

/// <summary>A dim "(nothing here)" leaf shown when a loaded group turns out to be empty.</summary>
public sealed class EmptyNode : SchemaTreeNode;
