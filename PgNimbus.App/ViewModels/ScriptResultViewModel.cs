using Avalonia.Collections;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// The materialized result of one statement in a multi-statement script,
/// displayed as its own selectable section (the strip above the results grid).
/// Immutable display data — selecting it re-points the shared grid at these rows.
/// </summary>
public sealed class ScriptResultViewModel
{
    private ScriptResultViewModel(
        int index,
        string sql,
        string label,
        string summary,
        bool hasError,
        string statusText,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<string> columnNames,
        AvaloniaList<object?[]> rows,
        string? rowCountText,
        string? timingText,
        string? capText,
        ImportedPlan? plan = null)
    {
        Index = index;
        Sql = sql;
        Label = label;
        Summary = summary;
        HasError = hasError;
        StatusText = statusText;
        Columns = columns;
        ColumnNames = columnNames;
        Rows = rows;
        RowCountText = rowCountText;
        TimingText = timingText;
        CapText = capText;
        Plan = plan;
    }

    /// <summary>1-based position of this statement in the script.</summary>
    public int Index { get; }

    /// <summary>The statement that produced this section, as it appeared in the script.</summary>
    public string Sql { get; }

    /// <summary>Short strip label — "1 · SELECT" (statement number + command keyword).</summary>
    public string Label { get; }

    /// <summary>Secondary strip line — row count / rows affected / "Error".</summary>
    public string Summary { get; }

    public bool HasError { get; }

    // The four fields below are pushed onto the owning QueryViewModel's shared
    // status-bar segments and grid when this section is selected.
    public string StatusText { get; }
    public IReadOnlyList<ColumnInfo> Columns { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public AvaloniaList<object?[]> Rows { get; }
    public string? RowCountText { get; }
    public string? TimingText { get; }
    public string? CapText { get; }

    /// <summary>
    /// The plan this statement's output parses into when it was an <c>EXPLAIN</c>; null
    /// for every other statement. Selecting such a section shows the plan views rather
    /// than the section's <c>QUERY PLAN</c> text rows, so an EXPLAIN inside a script
    /// (the `SET work_mem = …; EXPLAIN …` shape) reads like one run on its own.
    /// </summary>
    public ImportedPlan? Plan { get; }

    public const int MaxDisplayRows = QueryViewModel.MaxDisplayRows;

    public static ScriptResultViewModel From(int index, string sql, StatementResult result)
    {
        var keyword = FirstKeyword(sql);
        var ms = result.Elapsed.TotalMilliseconds;

        switch (result)
        {
            case MaterializedResultSet set:
            {
                var names = set.Columns.Select(c => c.Name).ToList();
                var rowText = RowLabel(set.Rows.Count);
                var timeText = $"{ms:F0} ms";
                var capText = set.Truncated
                    ? $"capped at {MaxDisplayRows:N0} rows — refine the query for the full set"
                    : null;

                return new ScriptResultViewModel(
                    index,
                    sql,
                    $"{index} · {keyword}",
                    $"{rowText} · {timeText}",
                    hasError: false,
                    statusText: "Done",
                    columns: set.Columns,
                    columnNames: names,
                    rows: new AvaloniaList<object?[]>(set.Rows),
                    rowCountText: rowText,
                    timingText: timeText,
                    capText: capText,
                    plan: QueryViewModel.TryParsePlanOutput(sql, names, set.Rows));
            }

            case CommandResult command:
            {
                var affected = RowLabel(command.RowsAffected);
                var timeText = $"{ms:F0} ms";
                return new ScriptResultViewModel(
                    index,
                    sql,
                    $"{index} · {command.CommandTag}",
                    $"{affected} affected · {timeText}",
                    hasError: false,
                    statusText: command.CommandTag,
                    columns: [],
                    columnNames: [],
                    rows: [],
                    rowCountText: $"{affected} affected",
                    timingText: timeText,
                    capText: null);
            }

            case QueryError error:
            {
                return new ScriptResultViewModel(
                    index,
                    sql,
                    $"{index} · {keyword}",
                    "Error",
                    hasError: true,
                    statusText: error.RolledBack
                        ? $"Error: {error.Message} — transaction rolled back"
                        : $"Error: {error.Message}",
                    columns: [],
                    columnNames: [],
                    rows: [],
                    rowCountText: null,
                    timingText: null,
                    capText: null);
            }

            default:
                return new ScriptResultViewModel(
                    index, sql, $"{index}", string.Empty, false, string.Empty, [], [], [], null, null, null);
        }
    }

    private static string RowLabel(long count) => count == 1 ? "1 row" : $"{count:N0} rows";

    // The leading SQL keyword (SELECT / INSERT / UPDATE / …), upper-cased, as the
    // statement's short label. Cheap span scan — no need for the tab-title regex.
    private static string FirstKeyword(string sql)
    {
        var span = sql.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && (char.IsLetter(span[end]) || span[end] == '_'))
        {
            end++;
        }

        return end == 0 ? "SQL" : span[..end].ToString().ToUpperInvariant();
    }
}
