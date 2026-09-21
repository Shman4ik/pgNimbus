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
/// controls), and a <c>WHERE</c> built from the filter chips' typed conditions
/// (<see cref="Filters"/>) plus an optional raw predicate seeded by foreign-key
/// navigation (<see cref="FilterText"/>). The composed SQL lands in the editor,
/// so what ran is always on screen: edit it and run, and the tab becomes a
/// plain query — the chips go with browse mode, and nothing ever
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
    [NotifyPropertyChangedFor(nameof(HasRawFilter), nameof(IsFilterBarVisible), nameof(WhereSql))]
    private string _filterText = string.Empty;

    /// <summary>
    /// The conditions the rows are filtered by, one chip each. Only committed
    /// conditions live here — a condition being written sits in <see cref="Draft"/>
    /// until Apply — so paging, sorting and reloads always run exactly what the
    /// chips say. A chip is never edited in place: editing works on a copy and
    /// swaps it in on Apply.
    /// </summary>
    public ObservableCollection<BrowseFilterViewModel> Filters { get; } = [];

    /// <summary>The condition open in the filter editor (the chip flyout), or null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingExisting), nameof(DraftPreviewSql))]
    private BrowseFilterViewModel? _draft;

    // The chip the draft will replace on Apply; null when the draft is a new condition.
    private BrowseFilterViewModel? _editing;

    /// <summary>True when the draft edits an existing chip, which is when the editor offers Remove.</summary>
    public bool IsEditingExisting => _editing is not null && Draft is not null;

    /// <summary>Raised when a draft is applied, so the view can close its editor.</summary>
    public event Action? DraftCommitted;

    /// <summary>
    /// Set while the user is adding the first condition, so the chip strip
    /// appears to anchor the editor before there's a chip to show. Cleared
    /// when that editor closes without adding one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterBarVisible))]
    private bool _isFilterBarOpen;

    /// <summary>
    /// The chip strip shows while there's something in it — a condition, the
    /// FK-seeded condition — or one is being added. A filtered grid with
    /// nothing on screen saying so reads as missing data.
    /// </summary>
    public bool IsFilterBarVisible => IsFilterBarOpen || HasActiveFilters || HasRawFilter;

    public bool HasRawFilter => FilterText.Trim().Length > 0;

    /// <summary>True when the running query carries typed conditions.</summary>
    public bool HasActiveFilters => Filters.Count > 0;

    /// <summary>Why the draft can't be applied, or null.</summary>
    [ObservableProperty]
    private string? _filterError;

    /// <summary>The whole <c>WHERE</c> the chips add, one line; the strip's tooltip. Null when nothing filters.</summary>
    public string? WhereSql =>
        WhereBody(Filters.Select(f => PredicateOf(f.ToFilter())).OfType<string>()) is { } body
            ? "WHERE " + body.Replace("\n  AND ", " AND ", StringComparison.Ordinal)
            : null;

    /// <summary>
    /// What Apply would add for the draft, shown in the editor before it runs —
    /// the generated SQL, visible up front. A hint while the draft isn't valid.
    /// </summary>
    public string DraftPreviewSql => Draft?.Predicate() ?? "Pick a column, a comparison and a value";

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

        if (WhereBody(Filters.Select(f => PredicateOf(f.ToFilter())).OfType<string>()) is { } where)
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

    // --- Filter chips --------------------------------------------------------

    /// <summary>
    /// Opens the editor on a new condition for <paramref name="column"/> (else
    /// the first column). Nothing runs until <see cref="CommitDraftCommand"/>.
    /// </summary>
    public BrowseFilterViewModel? BeginNewFilter(string? column = null, FilterOperator? op = null, string? value = null)
    {
        var name = column is not null && Columns.Any(c => c.Name == column) ? column : Columns.FirstOrDefault()?.Name;
        if (name is null)
        {
            return null;
        }

        IsFilterBarOpen = true;
        _editing = null;
        SetDraft(new BrowseFilterViewModel(Columns, name, op, value));
        return Draft;
    }

    /// <summary>Opens the editor on a copy of <paramref name="chip"/>; Apply swaps the copy in.</summary>
    public BrowseFilterViewModel BeginEditFilter(BrowseFilterViewModel chip)
    {
        var applied = chip.ToFilter();
        _editing = chip;
        SetDraft(new BrowseFilterViewModel(Columns, applied.Column, applied.Operator, applied.Value));
        return Draft!;
    }

    /// <summary>The editor closed without Apply: drop the draft, and the strip if it was only open for it.</summary>
    public void CancelDraft()
    {
        _editing = null;
        SetDraft(null);
        FilterError = null;
        IsFilterBarOpen = false;
    }

    /// <summary>
    /// Applies the draft: validates it, adds it as a chip (or replaces the chip
    /// it was copied from) and re-queries page 1. Refused with
    /// <see cref="FilterError"/> when the draft isn't valid, so nothing runs.
    /// </summary>
    [RelayCommand]
    private Task CommitDraftAsync()
    {
        if (Draft is not { } draft)
        {
            return Task.CompletedTask;
        }

        if (draft.Validate() is { } error)
        {
            FilterError = error;
            return Task.CompletedTask;
        }

        if (_editing is { } chip && Filters.IndexOf(chip) is var index and >= 0)
        {
            Filters[index] = draft;
        }
        else
        {
            Filters.Add(draft);
        }

        _editing = null;
        SetDraft(null);
        IsFilterBarOpen = false;
        DraftCommitted?.Invoke();
        return ReloadFilteredAsync();
    }

    /// <summary>Drops one chip (the ✕, or Remove in its editor) and re-queries.</summary>
    [RelayCommand]
    private Task RemoveFilterAsync(BrowseFilterViewModel? chip)
    {
        chip ??= _editing;
        if (chip is null || !Filters.Remove(chip))
        {
            return Task.CompletedTask;
        }

        if (ReferenceEquals(chip, _editing))
        {
            _editing = null;
            SetDraft(null);
            DraftCommitted?.Invoke();
        }

        return ReloadFilteredAsync();
    }

    /// <summary>"Filter by this cell": one new chip, applied at once.</summary>
    public Task AddAndApplyFilterAsync(string column, FilterOperator op, string? value)
    {
        if (!Columns.Any(c => c.Name == column))
        {
            return Task.CompletedTask;
        }

        Filters.Add(new BrowseFilterViewModel(Columns, column, op, value));
        return ReloadFilteredAsync();
    }

    /// <summary>Removes every condition, the FK-seeded one included, and reloads.</summary>
    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        var wasFiltering = HasActiveFilters || HasRawFilter;
        Filters.Clear();
        FilterText = string.Empty;
        CancelDraft();
        NotifyFiltersChanged();
        if (!wasFiltering)
        {
            return Task.CompletedTask;
        }

        Offset = 0;
        return LoadAsync();
    }

    /// <summary>
    /// Drops the typed conditions only — what turning the feature off does. The
    /// FK-seeded condition predates the chips and stays in the page SQL.
    /// </summary>
    public Task DropTypedFiltersAsync()
    {
        CancelDraft();
        if (Filters.Count == 0)
        {
            return Task.CompletedTask;
        }

        Filters.Clear();
        return ReloadFilteredAsync();
    }

    /// <summary>Drops the FK-seeded raw condition and reloads.</summary>
    [RelayCommand]
    private Task ClearRawFilterAsync()
    {
        FilterText = string.Empty;
        Offset = 0;
        return LoadAsync();
    }

    private Task ReloadFilteredAsync()
    {
        FilterError = null;
        NotifyFiltersChanged();
        Offset = 0;
        return LoadAsync();
    }

    private void NotifyFiltersChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(IsFilterBarVisible));
        OnPropertyChanged(nameof(WhereSql));
    }

    private void SetDraft(BrowseFilterViewModel? draft)
    {
        if (Draft is { } old)
        {
            old.Changed -= OnDraftChanged;
        }

        Draft = draft;
        if (draft is not null)
        {
            draft.Changed += OnDraftChanged;
        }

        FilterError = null;
        OnPropertyChanged(nameof(IsEditingExisting));
    }

    private void OnDraftChanged()
    {
        FilterError = null;
        OnPropertyChanged(nameof(DraftPreviewSql));
    }

    private string? PredicateOf(RowFilter filter) =>
        Columns.FirstOrDefault(c => c.Name == filter.Column) is { } column
            ? RowFilterSql.ToPredicate(filter, column.Editor, column.DataType)
            : null;

    // The raw FK condition first, then the typed conditions, ANDed.
    private string? WhereBody(IEnumerable<string> predicates) =>
        RowFilterSql.Combine(HasRawFilter ? predicates.Prepend(FilterText) : predicates);

    partial void OnCanGoNextChanged(bool value) => NextPageCommand.NotifyCanExecuteChanged();

    partial void OnCanGoPreviousChanged(bool value) => PreviousPageCommand.NotifyCanExecuteChanged();
}
