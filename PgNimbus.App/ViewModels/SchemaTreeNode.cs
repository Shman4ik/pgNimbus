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

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    public string Name { get; init; } = string.Empty;

    public ObservableCollection<SchemaTreeNode> Children { get; } = [];

    /// <summary>Seeds a placeholder child so an as-yet-unloaded expandable node still shows an expand arrow.</summary>
    protected void MarkExpandable() => Children.Add(new PlaceholderNode());

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
