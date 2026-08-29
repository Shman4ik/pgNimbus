using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Drives no-SQL "browse a table" mode: <c>ORDER BY</c> from clicking a column
/// header and <c>LIMIT</c>/<c>OFFSET</c> paging (the status bar's page
/// controls), plus an optional <c>WHERE</c> predicate seeded by foreign-key
/// navigation. There is no filter UI — the composed SQL lands in the editor,
/// which *is* the filter box: edit it and run, and the tab becomes a plain
/// query. Everything is pushed down to Postgres — no client-side slicing — so
/// browsing a billion-row table stays as cheap as one page. The owning
/// <see cref="QueryViewModel"/> supplies <see cref="_execute"/>, which composes
/// nothing itself: it just runs the SQL this view-model builds and reports how
/// many rows came back.
/// </summary>
public sealed partial class TableBrowseViewModel(string schema, string name, IReadOnlyList<string> pkColumns, Func<string, Task<int>> execute) : ObservableObject
{
    /// <summary>Rows fetched per page. One page past the fold is never loaded; paging is server-side.</summary>
    public const int PageSize = 100;

    // Runs the composed SQL through the owning tab's normal streaming path and
    // returns the number of rows the grid ended up showing.
    private readonly Func<string, Task<int>> _execute = execute;

    public string Schema { get; } = schema;

    public string Name { get; } = name;

    /// <summary>Raw SQL predicate (the text after <c>WHERE</c>), seeded by FK navigation. Empty means no filter.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

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
    private readonly IReadOnlyList<string> _pkColumns = pkColumns;

    /// <summary>
    /// Composes the page query. Identifiers are quoted; the filter is inlined
    /// verbatim (this is a SQL client — the user is trusted to write a predicate,
    /// same as typing it into the editor).
    /// </summary>
    public string BuildSql()
    {
        var sb = new StringBuilder();
        sb.Append("SELECT * FROM ")
          .Append(SqlIdentifier.Quote(Schema)).Append('.').Append(SqlIdentifier.Quote(Name));

        var filter = FilterText.Trim();
        if (filter.Length > 0)
        {
            sb.Append("\nWHERE ").Append(filter);
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

        PageLabel = count == 0
            ? (Offset > 0 ? "No more rows" : "No rows")
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

    partial void OnCanGoNextChanged(bool value) => NextPageCommand.NotifyCanExecuteChanged();

    partial void OnCanGoPreviousChanged(bool value) => PreviousPageCommand.NotifyCanExecuteChanged();
}
