using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed partial class SchemaTreeViewModel : ObservableObject
{
    private readonly SchemaService _schemaService;
    private readonly Action<bool>? _persistShowAdvanced;
    private readonly Action<bool>? _persistShowSizes;

    // The whole catalog's relations, fetched once on the first filter keystroke
    // and reused until the tree is refreshed. Held as the in-flight task rather
    // than the result so a burst of keystrokes shares one round trip.
    private Task<IReadOnlyList<RelationInfo>>? _relationsFetch;

    // Bumped per filter pass, so a slow catalog fetch that returns after the
    // user has typed on (or cleared the box) is discarded instead of painting
    // a stale filter over the tree.
    private int _filterGeneration;

    // Schemas the filter expanded by itself to put a deep match on screen.
    // Tracked so the reveal can be undone: a one-character query matches a table
    // in nearly every schema, and without this the whole tree was left open —
    // clearing the box put the rows back but never the expansion, so the sidebar
    // looked like it had "remembered" a state nobody asked for.
    private readonly HashSet<SchemaNode> _autoExpanded = [];

    // Node paths ("billing", "billing.invoices") the user opened by hand, as
    // opposed to the ones above. Kept because every node is rebuilt from scratch
    // on a refresh, which would otherwise collapse everything they had open.
    private readonly HashSet<string> _userExpanded = new(StringComparer.Ordinal);

    // Set while this view model is the one assigning IsExpanded (a filter reveal,
    // a post-refresh restore), so those don't get recorded as the user's own.
    private bool _restoringExpansion;

    // True between the first synchronous filter pass and the catalog snapshot
    // arriving. Until then "nothing matched" isn't a fact yet — the schemas
    // nobody expanded haven't been consulted — so the empty-state cue is held
    // back rather than flashing on the first keystroke against a remote server.
    private bool _awaitingCatalog;

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

    /// <summary>
    /// True when a filter is typed and nothing in the tree survives it. The
    /// panel is then genuinely empty, which on its own reads as a broken
    /// sidebar, so the view shows an explicit cue instead. (It used to read as
    /// "found: Roles" — the root groups were exempt from the filter entirely.)
    /// </summary>
    public bool ShowNoMatches =>
        !_awaitingCatalog && FilterText.Trim().Length > 0 && !Schemas.Any(n => n.IsFilteredIn);

    public string NoMatchesMessage => $"Nothing matches “{FilterText.Trim()}”";

    private void NotifyFilterOutcomeChanged()
    {
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(NoMatchesMessage));
    }

    /// <summary>Case-insensitive substring typed into the sidebar filter box. Filters schemas and their tables live, expanded or not.</summary>
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

    /// <summary>Opens a CREATE TABLE starter statement for a schema in a new query tab.</summary>
    public Func<SchemaNode, Task>? NewTableRequested { get; set; }

    /// <summary>DROP SCHEMA (the bool is CASCADE), then reload the tree and the caches derived from it.</summary>
    public Func<SchemaNode, bool, Task>? DropSchemaRequested { get; set; }

    /// <summary>Adds/removes a schema from the editor completion's exclusion set (persisted per connection by the host).</summary>
    public Func<SchemaNode, bool, Task>? SetSchemaExcludedFromCompletionRequested { get; set; }

    /// <summary>
    /// Opens the Roles &amp; Permissions window, optionally on a named role.
    /// The tree lists roles but can only ever show their headline attributes;
    /// the window is where "what can this role actually do" gets answered, so
    /// the node is a route to it rather than a dead end.
    /// </summary>
    public Func<string?, Task>? ManageRolesRequested { get; set; }

    /// <summary>
    /// Reloads the sidebar's Roles group after a role was created, altered or
    /// dropped elsewhere, so the tree does not keep showing a role that no
    /// longer exists. Only touches that one node — a full catalog refresh would
    /// collapse the tree the user is working in.
    /// </summary>
    public Task RefreshRolesAsync() =>
        Schemas.OfType<RolesGroupNode>().FirstOrDefault() is { IsLoaded: true } roles
            ? roles.RefreshAsync()
            : Task.CompletedTask;

    /// <summary>
    /// Every relation in the database, schema-qualified — what the filter box
    /// matches against so a table in a schema the user has never expanded is
    /// still findable (the command palette already searched this list, which is
    /// why a table it found could be missing from the sidebar). Host-supplied so
    /// the sidebar and the palette share one cached snapshot; falls back to the
    /// schema service when nothing is wired (design time, tests).
    /// </summary>
    public Func<Task<IReadOnlyList<RelationInfo>>>? AllRelationsRequested { get; set; }

    /// <summary>
    /// Whether a schema name is currently excluded from completion. Consulted
    /// when <see cref="RefreshAsync"/> rebuilds the nodes, so a refresh (or a
    /// reconnect) doesn't lose the markers the host persisted.
    /// </summary>
    public Func<string, bool>? IsSchemaExcludedFromCompletion { get; set; }

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

        // A schema the filter revealed from the catalog is expanded to show its
        // match, and its children arrive later, all defaulting to visible. Watch
        // for that so the newly loaded rows are vetted against the live filter
        // instead of the schema flashing open with every table it owns.
        Schemas.CollectionChanged += OnSchemasChanged;
    }

    private void OnSchemasChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var node in e.NewItems?.OfType<SchemaTreeNode>() ?? [])
        {
            // Not unsubscribed on removal: both handlers are reached only through
            // the discarded node's own events, so they die with the node (it keeps
            // this view model alive, never the other way round).
            TrackExpansion(node, node.Name);

            if (node is SchemaNode schema)
            {
                schema.Children.CollectionChanged += (_, args) => OnSchemaChildrenChanged(schema, args);
            }

            // A refresh rebuilds every node, so this is also where a tree the user
            // had opened comes back: reopening the schema loads its tables, and the
            // tables they had open are reopened as those arrive.
            if (_userExpanded.Contains(node.Name))
            {
                SetExpanded(node, true);
            }
        }
    }

    /// <summary>
    /// Records a node's hand-made expansions under <paramref name="path"/>, so a
    /// refresh (which rebuilds every node) can reopen what the user had open.
    /// Expansions this view model makes itself are skipped — a filter reveal is
    /// undone when the filter goes away, and must not outlive it as if the user
    /// had opened the schema.
    /// </summary>
    private void TrackExpansion(SchemaTreeNode node, string path)
    {
        node.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SchemaTreeNode.IsExpanded) || _restoringExpansion)
            {
                return;
            }

            if (node.IsExpanded)
            {
                _userExpanded.Add(path);
            }
            else
            {
                _userExpanded.Remove(path);
            }
        };
    }

    private void OnSchemaChildrenChanged(SchemaNode schema, NotifyCollectionChangedEventArgs e)
    {
        foreach (var table in e.NewItems?.OfType<TableNode>() ?? [])
        {
            var path = $"{schema.Name}.{table.Name}";
            TrackExpansion(table, path);

            // The children of a schema reopened after a refresh land here; a table
            // the user had open is reopened with them.
            if (_userExpanded.Contains(path))
            {
                SetExpanded(table, true);
            }
        }

        var query = FilterText.Trim();
        if (query.Length == 0)
        {
            return;
        }

        // The pass that expanded this schema judged it by the catalog snapshot,
        // because it had no children yet. Now it has: re-answer the whole question
        // for it, or a schema whose rows arrive after the next keystroke stays
        // hidden with its match inside it.
        ApplyReveal(schema, ApplySchemaFilter(schema, query, CachedRelations));
        NotifyFilterOutcomeChanged();
    }

    /// <summary>Assigns <see cref="SchemaTreeNode.IsExpanded"/> without it counting as the user's own doing.</summary>
    private void SetExpanded(SchemaTreeNode node, bool expanded)
    {
        _restoringExpansion = true;
        try
        {
            node.IsExpanded = expanded;
        }
        finally
        {
            _restoringExpansion = false;
        }
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

    /// <summary>
    /// Opens every schema. Deliberately one level deep: a schema loads its tables
    /// on first expand, so this already costs a catalog query per schema — walking
    /// on into each table's own sub-groups would multiply that by every table in
    /// the database.
    /// </summary>
    [RelayCommand]
    private void ExpandAll()
    {
        // Not routed through SetExpanded: this is the user opening them, so it is
        // recorded as such and survives a refresh.
        foreach (var schema in Schemas.OfType<SchemaNode>())
        {
            schema.IsExpanded = true;
        }
    }

    /// <summary>Closes the whole tree, however deep it was opened.</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Schemas)
        {
            Collapse(node);
        }

        // Each collapse above already dropped its own path from _userExpanded;
        // the filter's reveals have to be forgotten here.
        _autoExpanded.Clear();
    }

    private static void Collapse(SchemaTreeNode node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children)
        {
            Collapse(child);
        }
    }

    partial void OnFilterTextChanged(string value) => _ = ApplyFilterAsync();

    /// <summary>
    /// Runs a filter pass, then a second one once the catalog snapshot is in
    /// hand. The first pass is synchronous so typing stays responsive with what
    /// is already loaded; the second is what makes a table in a collapsed schema
    /// findable at all, and costs one catalog query per connection.
    /// </summary>
    private async Task ApplyFilterAsync()
    {
        var generation = ++_filterGeneration;
        var query = FilterText.Trim();
        _awaitingCatalog = query.Length > 0 && _relationsFetch is not { IsCompletedSuccessfully: true };
        ApplyFilter();

        if (query.Length == 0)
        {
            return;
        }

        var relations = await GetRelationsAsync();

        // The user typed on while the catalog was in flight: that later pass owns
        // the tree now, including the empty-state cue.
        if (generation != _filterGeneration)
        {
            return;
        }

        _awaitingCatalog = false;

        // No catalog (offline, or a failed query): the synchronous pass above
        // already stands, and its verdict is now final.
        if (relations is null)
        {
            NotifyFilterOutcomeChanged();
            return;
        }

        ApplyFilter(relations);
    }

    private async Task<IReadOnlyList<RelationInfo>?> GetRelationsAsync()
    {
        try
        {
            return await (_relationsFetch ??= AllRelationsRequested is null
                ? _schemaService.GetAllRelationsAsync(CancellationToken.None)
                : AllRelationsRequested());
        }
        catch
        {
            // Not the sidebar's error to report — the tree itself loaded fine and
            // the filter degrades to what is on screen. Drop the failed task so
            // the next keystroke retries.
            _relationsFetch = null;
            return null;
        }
    }

    private IReadOnlyList<RelationInfo>? CachedRelations =>
        _relationsFetch is { IsCompletedSuccessfully: true } fetch ? fetch.Result : null;

    private void ApplyFilter() => ApplyFilter(CachedRelations);

    /// <summary>
    /// Walks the tree and toggles <see cref="SchemaTreeNode.IsFilteredIn"/> so a schema shows when its own name
    /// matches (all its tables stay visible) or when a table inside it matches (only the matches show, and the
    /// schema auto-expands to reveal them). An empty filter reveals everything. Only schema and table names
    /// participate.
    /// </summary>
    /// <param name="relations">
    /// The whole catalog, when it has been fetched. Without it only already-loaded tables can be matched, which
    /// is what made a table in a never-expanded schema look like it did not exist while the command palette —
    /// which searches this same list — found it. A loaded schema is still judged by its own children: they are
    /// the fresher of the two, so a table dropped since the snapshot doesn't keep its schema on screen.
    /// </param>
    private void ApplyFilter(IReadOnlyList<RelationInfo>? relations)
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

            // Put back what the filter opened. Only the schemas it opened itself
            // are closed again, so a schema the user had open (before or during
            // the filter) stays open.
            foreach (var schema in _autoExpanded)
            {
                SetExpanded(schema, false);
            }

            _autoExpanded.Clear();
            NotifyFilterOutcomeChanged();
            return;
        }

        foreach (var node in Schemas)
        {
            // The root-level Extensions/Roles groups aren't schemas and their
            // children aren't tables, so the only thing a schema/table filter can
            // say about them is whether the group's own name matches. Exempting
            // them outright is what made a filter that found nothing read as
            // "found: Roles" - the one row left on screen.
            if (node is not SchemaNode schema)
            {
                node.IsFilteredIn = Contains(node.Name, query);
                foreach (var child in node.Children)
                {
                    child.IsFilteredIn = true;
                }

                continue;
            }

            ApplyReveal(schema, ApplySchemaFilter(schema, query, relations));
        }

        NotifyFilterOutcomeChanged();
    }

    /// <summary>
    /// Answers the filter for one schema and its loaded children, and reports
    /// whether it survives only because of something inside it (so the caller
    /// knows to open it).
    /// </summary>
    private static bool ApplySchemaFilter(SchemaNode schema, string query, IReadOnlyList<RelationInfo>? relations)
    {
        var schemaMatches = Contains(schema.Name, query);
        var anyTableMatches = FilterChildren(schema, query, schemaMatches);

        // A schema with no tables on the tree yet - never opened, still loading,
        // or its load failed - can only be answered by the catalog snapshot.
        // Asking IsLoaded instead is what made a schema vanish mid-load: expanding
        // sets that flag at once, so the snapshot standing in for the rows was
        // dropped a moment before the rows themselves arrived.
        var catalogMatches = !schema.Children.OfType<TableNode>().Any() && relations is not null &&
            relations.Any(r => r.Schema == schema.Name && Contains(r.Name, query));

        schema.IsFilteredIn = schemaMatches || anyTableMatches || catalogMatches;
        return (anyTableMatches || catalogMatches) && !schemaMatches;
    }

    /// <summary>
    /// Opens a schema whose only match is inside it, and closes it again when it
    /// stops matching — but only if the filter is what opened it in the first
    /// place. A schema the user opened by hand is theirs to close.
    /// </summary>
    private void ApplyReveal(SchemaNode schema, bool reveals)
    {
        if (reveals)
        {
            if (!schema.IsExpanded)
            {
                _autoExpanded.Add(schema);
                SetExpanded(schema, true);
            }
        }
        else if (_autoExpanded.Remove(schema))
        {
            // It stopped matching as the query grew: close it now rather than
            // waiting for the box to be cleared.
            SetExpanded(schema, false);
        }
    }

    /// <summary>
    /// Vets one schema's loaded children against the filter and reports whether any table matched.
    /// Called both from a full <see cref="ApplyFilter(IReadOnlyList{RelationInfo})"/> pass and on its
    /// own when a lazily-loaded schema's children arrive after the pass that expanded it.
    /// </summary>
    private static bool FilterChildren(SchemaNode schema, string query, bool schemaMatches)
    {
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

        return anyTableMatches;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        // The catalog snapshot the filter matches against is as stale as the tree
        // it came with; drop it so the next filter keystroke re-fetches.
        _relationsFetch = null;

        // The nodes below are replaced wholesale, so the filter's own reveals go
        // with them. What the user opened is remembered by path instead, and comes
        // back as the new nodes are added (OnSchemasChanged).
        _autoExpanded.Clear();

        try
        {
            var schemas = await _schemaService.GetSchemasAsync(CancellationToken.None);
            Schemas.Clear();
            foreach (var schema in schemas)
            {
                Schemas.Add(new SchemaNode(
                    _schemaService,
                    schema.Name,
                    () => ShowAdvancedObjects,
                    () => ShowSizes,
                    IsSchemaExcludedFromCompletion?.Invoke(schema.Name) ?? false));
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
