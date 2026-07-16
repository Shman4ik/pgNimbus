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
    public ColumnDetail? Column(string name) => Columns.FirstOrDefault(c => c.Name == name);
}
