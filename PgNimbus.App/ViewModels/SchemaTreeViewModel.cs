using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed partial class SchemaTreeViewModel : ObservableObject
{
    private readonly SchemaService _schemaService;
    private readonly Action<bool>? _persistShowAdvanced;
    private readonly Action<bool>? _persistShowSizes;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// True only during the first, empty load (no schemas yet) — drives the
    /// centered "Loading schema…" cue. A refresh with content already loaded
    /// keeps the tree visible and shows just the top progress bar instead, so
    /// the cue never renders on top of existing tree items. (During a refresh
    /// the old schemas stay in <see cref="Schemas"/> until the fetch returns,
    /// so the count is non-zero the whole time <see cref="IsLoading"/> is set.)
    /// </summary>
    public bool ShowInitialLoadingCue => IsLoading && Schemas.Count == 0;

    /// <summary>
    /// The thin top progress bar, shown only while *re*loading an already-populated
    /// tree (a refresh) — the mutually-exclusive counterpart to
    /// <see cref="ShowInitialLoadingCue"/>. On the first, empty load the centered
    /// cue carries the loading state alone, so the two never render at once.
    /// </summary>
    public bool ShowRefreshLoadingBar => IsLoading && Schemas.Count > 0;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInitialLoadingCue));
        OnPropertyChanged(nameof(ShowRefreshLoadingBar));
    }

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Case-insensitive substring typed into the sidebar filter box. Filters schemas and their already-loaded tables live.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>
    /// The sidebar's "advanced objects" toggle. Off (the default), the tree
    /// shows just schemas/tables and Roles; on, each schema gains its Functions,
    /// Sequences, and Types sub-groups, each table gains Indexes/Triggers
    /// sub-groups, and the root gains the Extensions group. Purely a declutter
    /// switch — the advanced groups are lazy either way, so the toggle never costs
    /// a catalog query by itself.
    /// </summary>
    [ObservableProperty]
    private bool _showAdvancedObjects;

    /// <summary>
    /// The sidebar's "show sizes" toggle. Off by default — when on, each table
    /// and matview row carries a dim on-disk size hint. Purely a display switch:
    /// the sizes are already fetched with the tree, so flipping it never costs a
    /// catalog query, just re-renders the loaded rows.
    /// </summary>
    [ObservableProperty]
    private bool _showSizes;

    public ObservableCollection<SchemaTreeNode> Schemas { get; } = [];

    public SchemaTreeViewModel(
        SchemaService schemaService,
        bool showAdvancedObjects = false,
        Action<bool>? persistShowAdvanced = null,
        bool showSizes = false,
        Action<bool>? persistShowSizes = null)
    {
        _schemaService = schemaService;
        _showAdvancedObjects = showAdvancedObjects;
        _persistShowAdvanced = persistShowAdvanced;
        _showSizes = showSizes;
        _persistShowSizes = persistShowSizes;
    }

    partial void OnShowSizesChanged(bool value)
    {
        _persistShowSizes?.Invoke(value);

        // Sizes ride along with the tree already, so flipping the toggle just
        // re-renders the loaded rows in place — no refetch.
        foreach (var schema in Schemas.OfType<SchemaNode>())
        {
            foreach (var table in schema.Children.OfType<TableNode>())
            {
                table.NotifySizeVisibilityChanged();
            }
        }
    }

    partial void OnShowAdvancedObjectsChanged(bool value)
    {
        _persistShowAdvanced?.Invoke(value);

        // Flip the already-loaded tree in place rather than refetching: the
        // Extensions group slots in right before Roles, and each loaded schema
        // adds/drops its Functions sub-group.
        var extensions = Schemas.OfType<ExtensionsGroupNode>().FirstOrDefault();
        if (value && extensions is null)
        {
            var roles = Schemas.OfType<RolesGroupNode>().FirstOrDefault();
            Schemas.Insert(roles is null ? Schemas.Count : Schemas.IndexOf(roles), new ExtensionsGroupNode(_schemaService));
        }
        else if (!value && extensions is not null)
        {
            Schemas.Remove(extensions);
        }

        foreach (var schema in Schemas.OfType<SchemaNode>())
        {
            schema.SetAdvancedGroupsVisible(value);
        }

        // Newly inserted nodes default to visible; a live filter has to vet them.
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Walks the loaded tree and toggles <see cref="SchemaTreeNode.IsFilteredIn"/> so a schema shows when its
    /// own name matches (all its tables stay visible) or when any loaded table matches (only the matches show,
    /// and the schema auto-expands to reveal them). An empty filter reveals everything. Only schema and table
    /// names participate; unloaded (lazily-expandable) tables can't be matched until their schema is expanded.
    /// </summary>
    private void ApplyFilter()
    {
        var query = FilterText.Trim();

        if (query.Length == 0)
        {
            foreach (var node in Schemas)
            {
                node.IsFilteredIn = true;
                foreach (var child in node.Children)
                {
                    child.IsFilteredIn = true;
                }
            }

            return;
        }

        foreach (var node in Schemas)
        {
            // The root-level Extensions/Roles groups aren't schemas and their
            // children aren't tables, so a schema/table-name filter has nothing
            // to say about them - keep them in rather than hiding them outright.
            if (node is not SchemaNode schema)
            {
                node.IsFilteredIn = true;
                foreach (var child in node.Children)
                {
                    child.IsFilteredIn = true;
                }

                continue;
            }

            var schemaMatches = Contains(schema.Name, query);
            var anyTableMatches = false;

            foreach (var child in schema.Children)
            {
                // Only real tables are matched by name. Sub-groups (Functions)
                // and placeholder/error rows aren't tables and have no name to
                // match, so they ride the schema's own visibility instead of
                // being filtered out.
                var tableMatches = child is TableNode && Contains(child.Name, query);
                child.IsFilteredIn = schemaMatches || tableMatches || child is not TableNode;
                anyTableMatches |= tableMatches;
            }

            schema.IsFilteredIn = schemaMatches || anyTableMatches;

            // Reveal deep matches: if the schema only survives because a table inside it matched, expand it.
            if (anyTableMatches && !schemaMatches)
            {
                schema.IsExpanded = true;
            }
        }
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var schemas = await _schemaService.GetSchemasAsync(CancellationToken.None);
            Schemas.Clear();
            foreach (var schema in schemas)
            {
                Schemas.Add(new SchemaNode(_schemaService, schema.Name, () => ShowAdvancedObjects, () => ShowSizes));
            }

            // Server-wide groups after the schemas, both lazily loaded.
            // Extensions is advanced-only; Roles is always shown.
            if (ShowAdvancedObjects)
            {
                Schemas.Add(new ExtensionsGroupNode(_schemaService));
            }

            Schemas.Add(new RolesGroupNode(_schemaService));

            // A fresh catalog invalidates any prior filter pass; re-apply so a lingering query still holds.
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
