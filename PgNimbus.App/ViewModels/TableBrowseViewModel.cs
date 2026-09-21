using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Drives no-SQL "browse a table" mode: <c>ORDER BY</c> from clicking a column
/// header and <c>LIMIT</c>/<c>OFFSET</c> paging (the status bar's page
/// controls), and a <c>WHERE</c> built from the filter bar's typed predicates
/// (<see cref="Filters"/>) plus an optional raw predicate seeded by foreign-key
/// navigation (<see cref="FilterText"/>). The composed SQL lands in the editor,
/// so what ran is always on screen: edit it and run, and the tab becomes a
/// plain query — the filter bar goes with browse mode, and nothing ever
/// rewrites a query the user typed. Everything is pushed down to Postgres — no
/// client-side slicing — so browsing a billion-row table stays as cheap as one
/// page. The owning <see cref="QueryViewModel"/> supplies <see cref="_execute"/>,
/// which composes nothing itself: it just runs the SQL this view-model builds
/// and reports how many rows came back.
/// </summary>
public sealed partial class TableBrowseViewModel(string schema, string name, IReadOnlyList<ColumnDetail> columns, Func<string, Task<int>> execute) : ObservableObject
{
    /// <summary>Rows fetched per page. One page past the fold is never loaded; paging is server-side.</summary>
    public const int PageSize = 100;

    // Runs the composed SQL through the owning tab's normal streaming path and
    // returns the number of rows the grid ended up showing.
    private readonly Func<string, Task<int>> _execute = execute;

    public string Schema { get; } = schema;

    public string Name { get; } = name;

    /// <summary>The browsed table's columns — what the filter bar offers and types its inputs by.</summary>
    public IReadOnlyList<ColumnDetail> Columns { get; } = columns;

    /// <summary>
    /// Raw SQL predicate (the text after <c>WHERE</c>), seeded by FK navigation.
    /// Empty means none. Shown in the filter bar as its own removable condition,
    /// ANDed with the typed filters.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRawFilter), nameof(IsFilterBarVisible), nameof(FilterPreviewSql))]
    private string _filterText = string.Empty;

    /// <summary>The filter bar's rows, as edited. Only <see cref="ApplyFiltersAsync"/> turns them into SQL that runs.</summary>
    public ObservableCollection<BrowseFilterViewModel> Filters { get; } = [];

    // What the page query actually filters on: the rows as of the last Apply.
    // Kept apart from Filters so paging, sorting and reloads keep running what
    // was applied while a half-typed row sits in the bar.
    private IReadOnlyList<RowFilter> _appliedFilters = [];

    /// <summary>Opened by Ctrl/Cmd+F in the grid, the palette or a quick filter; see <see cref="IsFilterBarVisible"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterBarVisible))]
    private bool _isFilterBarOpen;

    /// <summary>
    /// The bar shows while it's open <em>or</em> anything is filtering the rows:
    /// a filtered grid with nothing on screen saying so reads as missing data.
    /// </summary>
    public bool IsFilterBarVisible => IsFilterBarOpen || HasActiveFilters || HasRawFilter;

    public bool HasRawFilter => FilterText.Trim().Length > 0;

    /// <summary>True when the running query carries typed filters.</summary>
    public bool HasActiveFilters => _appliedFilters.Count > 0;

    /// <summary>Why Apply refused, or null.</summary>
    [ObservableProperty]
    private string? _filterError;

    /// <summary>
    /// The <c>WHERE</c> clause Apply would run, from the bar as it stands: the
    /// generated SQL, shown before it runs. Rows that aren't valid yet are
    /// left out (their error shows under their own input). One line — the bar
    /// has one line for it; the editor gets the laid-out form.
    /// </summary>
    public string FilterPreviewSql =>
        WhereBody(Filters.Select(f => f.Predicate()).OfType<string>()) is { } body
            ? "WHERE " + body.Replace("\n  AND ", " AND ", StringComparison.Ordinal)
            : "No filter: all rows";

    /// <summary>
    /// True when the bar says something different from what the rows are
    /// filtered by — the cue that Apply would change anything, and the one
    /// state in which it's the highlighted button.
    /// </summary>
    public bool HasUnappliedChanges =>
        !Filters.Select(f => f.ToFilter()).SequenceEqual(_appliedFilters);

    [ObservableProperty]
    private string? _sortColumn;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private int _offset;

    /// <summary>"Rows 101–200" style range label for the current page.</summary>
    [ObservableProperty]
    private string _pageLabel = string.Empty;

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _canGoNext;

    // Primary-key column names, in key order; empty for views / PK-less tables.
    private readonly IReadOnlyList<string> _pkColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

    /// <summary>
    /// Composes the page query. Identifiers are quoted; typed filters inline
    /// their values as quoted literals (<see cref="RowFilterSql"/>), and the raw
    /// FK predicate is inlined verbatim (this is a SQL client — the user is
    /// trusted to write a predicate, same as typing it into the editor).
    /// </summary>
    public string BuildSql()
    {
        var sb = new StringBuilder();
        sb.Append("SELECT * FROM ")
          .Append(SqlIdentifier.Quote(Schema)).Append('.').Append(SqlIdentifier.Quote(Name));

        if (WhereBody(_appliedFilters.Select(PredicateOf).OfType<string>()) is { } where)
        {
            sb.Append("\nWHERE ").Append(where);
        }

        if (SortColumn is { } column)
        {
            sb.Append("\nORDER BY ").Append(SqlIdentifier.Quote(column)).Append(SortDescending ? " DESC" : " ASC");
        }
        else if (_pkColumns.Count > 0)
        {
            // Default to primary-key order until a header click chooses one:
            // heap order surprises (updated rows jump to the end), and
            // LIMIT/OFFSET paging over an unordered scan may skip or repeat
            // rows between pages. Cheap — it's the PK index.
            sb.Append("\nORDER BY ").Append(string.Join(", ", _pkColumns.Select(SqlIdentifier.Quote)));
        }

        sb.Append("\nLIMIT ").Append(PageSize).Append(" OFFSET ").Append(Offset);
        return sb.ToString();
    }

    /// <summary>Runs the current page and refreshes the paging/sort labels and button state.</summary>
    public async Task LoadAsync()
    {
        var count = await _execute(BuildSql());

        CanGoPrevious = Offset > 0;
        // A full page came back, so there may be another — cheap heuristic that
        // avoids a separate COUNT(*) on every page turn.
        CanGoNext = count == PageSize;

        var filtered = HasActiveFilters || HasRawFilter;
        PageLabel = count == 0
            ? (Offset > 0 ? "No more rows" : filtered ? "No matching rows" : "No rows")
            : $"Rows {Offset + 1:N0}–{Offset + count:N0}";
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPage()
    {
        Offset += PageSize;
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPage()
    {
        Offset = Math.Max(0, Offset - PageSize);
        return LoadAsync();
    }

    /// <summary>
    /// Toggles the sort on <paramref name="column"/> (asc → desc → asc) and
    /// jumps back to the first page — invoked from a results-grid header click.
    /// </summary>
    public Task SortByAsync(string column)
    {
        if (string.Equals(SortColumn, column, StringComparison.Ordinal))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        Offset = 0;
        return LoadAsync();
    }

    // --- Filter bar -------------------------------------------------------

    /// <summary>
    /// Opens the filter bar, adding a first row (on <paramref name="column"/>,
    /// else the first column) when it has none. Returns the row to focus.
    /// </summary>
    public BrowseFilterViewModel? OpenFilterBar(string? column = null)
    {
        IsFilterBarOpen = true;
        return Filters.Count > 0 ? Filters[^1] : AddFilter(column);
    }

    /// <summary>Adds a row to the bar; nothing runs until Apply.</summary>
    public BrowseFilterViewModel? AddFilter(string? column = null, FilterOperator? op = null, string? value = null)
    {
        var name = column is not null && Columns.Any(c => c.Name == column) ? column : Columns.FirstOrDefault()?.Name;
        if (name is null)
        {
            return null;
        }

        var filter = new BrowseFilterViewModel(Columns, name, op, value);
        filter.Changed += OnDraftChanged;
        Filters.Add(filter);
        RenumberConnectors();
        OnDraftChanged();
        return filter;
    }

    [RelayCommand]
    private void AddFilterRow() => AddFilter();

    /// <summary>
    /// Drops a row. When what's left is valid it's applied at once: the ✕
    /// reads as "stop filtering on this", and leaving the rows filtered until
    /// a second click would contradict the bar.
    /// </summary>
    [RelayCommand]
    private Task RemoveFilterAsync(BrowseFilterViewModel filter)
    {
        filter.Changed -= OnDraftChanged;
        Filters.Remove(filter);
        RenumberConnectors();
        OnDraftChanged();
        return Filters.All(f => f.Validate() is null) ? ApplyFiltersAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// Runs the bar: validates every row, then re-queries page 1 with the new
    /// <c>WHERE</c>. Refused as a whole on any invalid row — applying the valid
    /// half would show rows filtered by less than the bar says.
    /// </summary>
    [RelayCommand]
    private Task ApplyFiltersAsync()
    {
        if (Filters.Select(f => f.Validate()).OfType<string>().FirstOrDefault() is { } error)
        {
            FilterError = error;
            return Task.CompletedTask;
        }

        FilterError = null;
        _appliedFilters = Filters.Select(f => f.ToFilter()).ToList();
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(HasUnappliedChanges));
        OnPropertyChanged(nameof(IsFilterBarVisible));
        Offset = 0;
        return LoadAsync();
    }

    /// <summary>"Filter by this cell": one new row, applied at once.</summary>
    public Task AddAndApplyFilterAsync(string column, FilterOperator op, string? value)
    {
        IsFilterBarOpen = true;
        AddFilter(column, op, value);
        return ApplyFiltersAsync();
    }

    /// <summary>Removes every filter, the FK-seeded condition included, closes the bar and reloads.</summary>
    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        var wasFiltering = HasActiveFilters || HasRawFilter;
        foreach (var filter in Filters)
        {
            filter.Changed -= OnDraftChanged;
        }

        Filters.Clear();
        FilterText = string.Empty;
        FilterError = null;
        IsFilterBarOpen = false;
        _appliedFilters = [];
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(HasUnappliedChanges));
        OnPropertyChanged(nameof(IsFilterBarVisible));
        OnDraftChanged();
        if (!wasFiltering)
        {
            return Task.CompletedTask;
        }

        Offset = 0;
        return LoadAsync();
    }

    /// <summary>Drops the FK-seeded raw condition and reloads.</summary>
    [RelayCommand]
    private Task ClearRawFilterAsync()
    {
        FilterText = string.Empty;
        Offset = 0;
        return LoadAsync();
    }

    private void OnDraftChanged()
    {
        FilterError = null;
        OnPropertyChanged(nameof(FilterPreviewSql));
        OnPropertyChanged(nameof(HasUnappliedChanges));
    }

    // The raw FK condition, when there is one, is the bar's first line, so every
    // typed row after it is an "and".
    private void RenumberConnectors()
    {
        for (var i = 0; i < Filters.Count; i++)
        {
            Filters[i].Connector = i == 0 && !HasRawFilter ? "where" : "and";
        }
    }

    partial void OnFilterTextChanged(string value) => RenumberConnectors();

    private string? PredicateOf(RowFilter filter) =>
        Columns.FirstOrDefault(c => c.Name == filter.Column) is { } column
            ? RowFilterSql.ToPredicate(filter, column.Editor, column.DataType)
            : null;

    // The raw FK condition first, then the typed filters, ANDed.
    private string? WhereBody(IEnumerable<string> predicates) =>
        RowFilterSql.Combine(HasRawFilter ? predicates.Prepend(FilterText) : predicates);

    partial void OnCanGoNextChanged(bool value) => NextPageCommand.NotifyCanExecuteChanged();

    partial void OnCanGoPreviousChanged(bool value) => PreviousPageCommand.NotifyCanExecuteChanged();
}
