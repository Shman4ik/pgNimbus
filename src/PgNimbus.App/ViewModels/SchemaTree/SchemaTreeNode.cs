using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Base node for the schema sidebar tree. Children load lazily on first
/// expand so opening a connection doesn't eagerly walk the whole catalog.
/// </summary>
public abstract partial class SchemaTreeNode : ObservableObject
{
    private bool _loaded;

    protected SchemaTreeNode()
    {
        Children.CollectionChanged += (_, _) => SyncShownChildren();
    }

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

    /// <summary>
    /// Whether this node passes the sidebar filter; true when no filter is active.
    /// A node that doesn't is left out of its parent's <see cref="ShownChildren"/>
    /// once the filter pass calls <see cref="SyncShownChildren"/> on the parent.
    /// </summary>
    [ObservableProperty]
    private bool _isFilteredIn = true;

    private string _name = string.Empty;
    private Func<string>? _nameText;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// Sets <see cref="Name"/> to words from the string table (a group's name, an
    /// "empty" row). <see cref="RefreshLanguage"/> computes it again after a change
    /// of language; <paramref name="text"/> must not capture anything (make it static).
    /// </summary>
    protected void SetLocalizedName(Func<string> text)
    {
        _nameText = text;
        Name = text();
    }

    /// <summary>
    /// Called by the tree's view model on every node it holds after a change of
    /// language: a node whose text is composed from <c>Strings</c> raises its
    /// change here. The default recomputes a name set with <see cref="SetLocalizedName"/>.
    /// </summary>
    public virtual void RefreshLanguage()
    {
        if (_nameText is { } text)
        {
            Name = text();
        }
    }

    /// <summary>Every child this node has loaded: the source of truth the filter reads.</summary>
    public RangeObservableCollection<SchemaTreeNode> Children { get; } = [];

    /// <summary>
    /// The children the tree shows: <see cref="Children"/> minus what the filter
    /// hides. The tree binds to this, not to <see cref="Children"/>, because its
    /// panels virtualize (a schema of 5,000 tables took 8 s to expand when every
    /// row was realized), and a virtualizing panel has to realize a hidden row to
    /// learn that it takes no space: a filter matching one table of 5,000 by
    /// visibility realized all of them. Left out of this list, it is never built.
    /// </summary>
    public RangeObservableCollection<SchemaTreeNode> ShownChildren { get; } = [];

    /// <summary>
    /// Brings <see cref="ShownChildren"/> in line with <see cref="Children"/> and
    /// their <see cref="IsFilteredIn"/>, with one Reset, and only when it differs
    /// (a Reset rebuilds the rows it holds). Runs by itself when the children
    /// change; a filter pass calls it after deciding a node's children.
    /// </summary>
    public void SyncShownChildren()
    {
        var shown = new List<SchemaTreeNode>(Children.Count);
        foreach (var child in Children)
        {
            if (child.IsFilteredIn)
            {
                shown.Add(child);
            }
        }

        if (shown.Count == ShownChildren.Count && shown.SequenceEqual(ShownChildren))
        {
            return;
        }

        ShownChildren.ReplaceAll(shown);
    }

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
        Children.ReplaceAll(children);
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
            // One Reset, not an Add per child: the sidebar filter re-vets a schema
            // on every change to its children, which per Add was quadratic.
            Children.ReplaceAll(await FetchChildrenAsync());
            _loaded = true;
        }
        catch (Exception ex)
        {
            // Not loaded: the next expand tries again. A failure is usually the
            // connection (a dropped VPN, a laptop waking up), and it used to stick:
            // the error row stayed under the node after the server was back and the
            // same table browsed fine, until a refresh of the whole tree.
            _loaded = false;
            Children.ReplaceAll([new ErrorNode { Name = ex.Message }]);
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
public sealed class EmptyNode : SchemaTreeNode
{
    public EmptyNode()
    {
    }

    public EmptyNode(Func<string> text) => SetLocalizedName(text);
}
