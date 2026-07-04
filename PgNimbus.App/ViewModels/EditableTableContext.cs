namespace PgNimbus.App.ViewModels;

/// <summary>
/// Tracks the single table a result set maps to, so inline cell edits can be
/// turned into targeted UPDATE statements. Cleared whenever the SQL text
/// changes, so edits are only ever attempted against the exact query that
/// produced this context (see <see cref="QueryViewModel"/>).
/// </summary>
public sealed record EditableTableContext(string Schema, string Table, IReadOnlyList<string> PrimaryKeyColumns);
