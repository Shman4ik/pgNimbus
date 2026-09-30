using System.Runtime.CompilerServices;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Tracks the single table a result set maps to, so inline cell edits can be
/// turned into targeted UPDATE statements. Established two ways: browse mode
/// knows its table up front, and a hand-typed query gets one after the run
/// when the wire metadata maps every column onto one table (see
/// <see cref="QueryViewModel"/>). Cleared whenever the SQL text changes or a
/// new run starts, so edits are only ever attempted against the exact query
/// that produced this context.
/// <see cref="Columns"/> carries the table's per-column type metadata — it
/// drives the grid's type-aware cell editors (enum dropdown, checkbox, date
/// picker) and tells the UPDATE which columns need their text parsed
/// server-side via a cast to the declared type.
/// </summary>
public sealed record EditableTableContext(
    string Schema,
    string Table,
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<ColumnDetail> Columns)
{
    // Staging a delete snapshots the row by asking for every column, and a delete
    // of the whole grid asks rows × columns times, so the lookup is a dictionary,
    // built once per Columns list. Kept beside the record rather than in a field:
    // a field would take part in the record's equality and ride along in a `with`.
    private static readonly ConditionalWeakTable<IReadOnlyList<ColumnDetail>, Dictionary<string, ColumnDetail>> ByName = new();

    public ColumnDetail? Column(string name) =>
        ByName.GetValue(Columns, static columns =>
        {
            var byName = new Dictionary<string, ColumnDetail>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                byName.TryAdd(column.Name, column);
            }

            return byName;
        }).GetValueOrDefault(name);
}
