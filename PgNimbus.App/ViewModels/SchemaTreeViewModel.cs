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

    // --- Recent relations -------------------------------------------------

    /// <summary>How many entries the pinned Recent section holds — enough for the handful of relations a task actually involves, short enough to stay scannable above the tree.</summary>
    private const int MaxRecent = 5;

    private readonly List<RelationInfo> _recent = [];

    private RecentGroupNode? _recentGroup;

    /// <summary>
    /// Records a relation as recently opened, floating it to the top of the
    /// pinned Recent section (see <see cref="RecentGroupNode"/>). Called from the
    /// host for every route that opens one — the tree's double-click, the palette's
    /// table jump, follow-FK, "Source (DDL)" — so Recent reflects what was worked
    /// on rather than how it was reached.
    ///
    /// Session-scoped by design: it is not persisted, and a schema refresh keeps it
    /// (the relations are still there). Re-recording an entry already at the top is
    /// a no-op, so re-opening the same table doesn't churn the list.
    /// </summary>
    public void RecordRecentRelation(string schema, string name, RelationKind kind)
    {
        var entry = new RelationInfo(schema, name, kind);
        if (_recent.Count > 0 && _recent[0] == entry)
        {
            return;
        }

        _recent.RemoveAll(r => r.Schema == schema && r.Name == name);
        _recent.Insert(0, entry);
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        }

        SyncRecentSection();
    }

    /// <summary>
    /// Keeps the pinned Recent node in step with <see cref="_recent"/>: present and
    /// expanded at the top of the tree once anything has been opened, absent before
    /// that (an empty section is pure noise). The node rebuilds its own children,
    /// so the same instance survives a refresh with its expansion state intact.
    /// </summary>
    private void SyncRecentSection()
    {
        if (_recent.Count == 0)
        {
            return;
        }

        if (_recentGroup is null)
        {
            _recentGroup = new RecentGroupNode(_schemaService, () => _recent, () => ShowAdvancedObjects);
            _recentGroup.IsExpanded = true;
        }

        if (Schemas.Count == 0 || Schemas[0] != _recentGroup)
        {
            Schemas.Remove(_recentGroup);
            Schemas.Insert(0, _recentGroup);
        }

        // Fire-and-forget like every other node refresh in this tree: the group
        // builds its children from the in-memory list, so there is nothing to
        // await and nothing that can fail.
        _ = _recentGroup.RefreshAsync();
        ApplyFilter();
    }

    // --- Host-supplied actions --------------------------------------------
    // The schema tree's context-menu / double-click / refresh actions are
    // window-level orchestration — opening query and browse tabs, refreshing
    // the autocomplete and palette caches alongside the tree, building an
    // Alter Table dialog VM — so MainViewModel wires them here after
    // construction. The SchemaTreePanel invokes them through this VM and never
    // reaches back into the host window, keeping the panel bound to just this
    // focused sub-ViewModel (UI design rule 7).

    /// <summary>Reloads the tree plus everything else derived from the catalog (autocomplete + palette). Backs <see cref="RefreshAllCommand"/>.</summary>
    public Func<Task>? RefreshAllRequested { get; set; }

    /// <summary>Opens a table/view's reconstructed CREATE DDL in a new query tab.</summary>
    public Func<TableNode, Task>? ShowTableSourceRequested { get; set; }

    /// <summary>Opens a browse (preview) tab for a table/view — the tree's double-click default action.</summary>
    public Func<TableNode, Task>? PreviewTableRequested { get; set; }

    /// <summary>Opens a function's source in a new query tab.</summary>
    public Func<FunctionNode, Task>? ShowFunctionSourceRequested { get; set; }

    /// <summary>Installs or drops an extension, then refreshes its node.</summary>
    public Func<ExtensionNode, bool, Task>? SetExtensionInstalledRequested { get; set; }

    /// <summary>Builds the Alter Table dialog's ViewModel for a table (the dialog itself is shown by the view).</summary>
    public Func<TableNode, AlterTableViewModel>? AlterTableViewModelFactory { get; set; }

    /// <summary>
    /// The sidebar refresh button. Reloads the tree together with everything
    /// else derived from the live catalog (autocomplete + palette) via the
    /// host-supplied <see cref="RefreshAllRequested"/>; falls back to a
    /// tree-only refresh when no host is wired (e.g. design time).
    /// </summary>
    [RelayCommand]
    private Task RefreshAll() => RefreshAllRequested?.Invoke() ?? RefreshAsync();

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
    /// and the schema auto-expands to reveal them). An empty filter reveals everything.
    ///
    /// A table matches on its own name <em>or</em> on any of its loaded column names, so "customer_id" finds
    /// the tables that reference a customer rather than only the table called customers — a table that
    /// survives on a column match expands to show why. Unloaded (lazily-expandable) nodes can't be matched
    /// until they're expanded, which applies to schemas and to a table's columns alike.
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
            // The pinned Recent group holds tables, so it filters like a schema
            // does - except its own name ("Recent") is chrome, not something to
            // match on, and an empty Recent section under a filter is noise.
            if (node is RecentGroupNode recent)
            {
                var anyRecentMatches = false;
                foreach (var child in recent.Children)
                {
                    var matches = child is TableNode table && TableMatches(table, query);
                    child.IsFilteredIn = matches || child is not TableNode;
                    anyRecentMatches |= matches;
                }

                recent.IsFilteredIn = anyRecentMatches;
                continue;
            }

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
                // Only real tables are matched. Sub-groups (Functions) and
                // placeholder/error rows aren't tables and have no name to
                // match, so they ride the schema's own visibility instead of
                // being filtered out.
                var tableMatches = child is TableNode table && TableMatches(table, query);
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

    /// <summary>
    /// A table matches on its own name, or on one of its already-loaded column
    /// names — expanding it in that case, since a row that matched on something
    /// invisible reads as a bug. Columns load on first expand, so an unexpanded
    /// table can only ever match by name.
    /// </summary>
    private static bool TableMatches(TableNode table, string query)
    {
        if (Contains(table.Name, query))
        {
            return true;
        }

        if (!table.Children.Any(c => c is ColumnNode column && Contains(column.Name, query)))
        {
            return false;
        }

        table.IsExpanded = true;
        return true;
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

            // Recent survives a refresh — the relations are still there, and
            // losing the section on every catalog reload would defeat it. The
            // node is re-inserted at the top and rebuilds its own children.
            SyncRecentSection();

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
