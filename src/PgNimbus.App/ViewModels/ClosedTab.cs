namespace PgNimbus.App.ViewModels;

/// <summary>
/// What Reopen Closed Tab puts back: everything a person put into the tab, not
/// what it happened to be showing. Results, plans and staged edits are not kept
/// (they belong to a run, and running again is one keystroke), and neither is
/// anything outside this window's session: the stack lives on
/// <see cref="MainViewModel"/> and goes with it.
/// </summary>
/// <param name="Title">The label the tab showed when it closed, for the status line.</param>
/// <param name="Index">Where the tab sat in the strip, so it comes back in its place.</param>
/// <param name="FileBaseline">The file content the dirty dot compared against, so a reopened file tab shows the dot as it was without reading the disk.</param>
public sealed record ClosedTab(
    int Index,
    string Title,
    string Sql,
    string DefaultTitle,
    string? TitleOverride,
    string? FilePath,
    string? FileBaseline,
    Guid? SavedQueryId,
    int CaretOffset,
    (string Schema, string Name)? BrowsedTable)
{
    public static ClosedTab From(QueryViewModel tab, int index) => new(
        index,
        tab.TabTitle,
        tab.Sql,
        tab.DefaultTitle,
        tab.TitleOverride,
        tab.FilePath,
        tab.FileBaseline,
        tab.SavedQueryId,
        tab.CaretOffset,
        tab.BrowsedTableName);
}
