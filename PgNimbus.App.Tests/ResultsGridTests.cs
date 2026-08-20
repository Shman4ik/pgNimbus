using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The results grid is built in code-behind, not XAML: columns are generated per
/// result set and bound through <c>RowIndexConverter</c> rather than indexer
/// paths (an AOT constraint). Nothing else checks that the generated columns
/// match the result they came from, and a wrong column count or a stale header
/// is exactly the sort of thing that reaches a release looking fine in a
/// screenshot of a *different* scenario.
/// </summary>
public class ResultsGridTests
{
    [Test]
    public async Task Grid_builds_one_column_per_result_column()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var grid = FindResultsGrid(window);
            await Assert.That(grid).IsNotNull();

            // Headers are built controls (type glyph + name), not strings, so
            // the name is read out of the header's own logical tree.
            var headers = grid!.Columns.Select(HeaderText).ToList();

            foreach (var name in vm.ActiveTab.ColumnNames)
            {
                await Assert.That(headers).Contains(name);
            }
        });
    }

    [Test]
    public async Task Grid_shows_every_row_of_the_result()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var grid = FindResultsGrid(window);
            await Assert.That(grid).IsNotNull();
            await Assert.That(grid!.ItemsSource).IsNotNull();

            var shown = grid.ItemsSource!.Cast<object>().Count();
            await Assert.That(shown).IsEqualTo(vm.ActiveTab.Rows.Count);
        });
    }

    /// <summary>
    /// Switching tabs has to re-point the grid at the new tab's result. The
    /// panel tracks the active tab itself (it is window-central, not per-tab),
    /// which is the part that can silently keep showing the previous tab's rows.
    /// </summary>
    [Test]
    public async Task Switching_to_an_empty_tab_clears_the_grid()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var grid = FindResultsGrid(window);
            await Assert.That(grid).IsNotNull();
            await Assert.That(grid!.ItemsSource!.Cast<object>().Count()).IsGreaterThan(0);

            vm.AddTabCommand.Execute(null);
            Ui.Settle();

            var rowsNow = grid.ItemsSource?.Cast<object>().Count() ?? 0;
            await Assert.That(rowsNow).IsEqualTo(0);
        });
    }

    private static DataGrid? FindResultsGrid(Window window) =>
        window.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();

    private static string? HeaderText(DataGridColumn column) => column.Header switch
    {
        string text => text,
        Control control => control.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
        _ => null,
    };
}
