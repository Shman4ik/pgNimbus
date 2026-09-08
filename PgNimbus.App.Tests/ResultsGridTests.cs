using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using PgNimbus.App.Converters;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
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

    /// <summary>
    /// Dragging a header's right edge resizes that column — and lifts the
    /// auto-width cap the columns are built with, which otherwise clamps the
    /// drag itself (DataGridColumnHeader clamps every step to MaxWidth), so a
    /// widening drag would stop dead partway with nothing on screen saying why.
    /// </summary>
    [Test]
    public async Task Dragging_a_column_edge_resizes_that_column()
    {
        await Ui.Run(async () =>
        {
            var (window, _) = Scenarios.Shell();
            Ui.Show(window);

            var grid = FindResultsGrid(window)!;
            var header = FindHeader(window, 0);
            await Assert.That(header).IsNotNull();

            var before = grid.Columns[0].ActualWidth;
            Drag(window, header!, by: 60);

            await Assert.That(grid.Columns[0].ActualWidth).IsGreaterThan(before);
            await Assert.That(double.IsPositiveInfinity(grid.Columns[0].MaxWidth)).IsTrue();

            window.Close();
        });
    }

    /// <summary>
    /// The case the cap is actually in the way of: a column whose content is
    /// long enough that auto-sizing parks it at the cap. Widening it has to
    /// work — a drag that stops at an invisible ceiling is the reported bug this
    /// feature would otherwise ship with.
    /// </summary>
    [Test]
    public async Task A_capped_column_can_be_dragged_past_the_cap()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            // One column, one very long value: auto-sizing wants far more than
            // the cap, so the column lands exactly on it.
            vm.ActiveTab.SeedResult(
                [new ColumnInfo("payload", "text", typeof(string))],
                [[new string('x', 400)]]);
            Ui.Settle();

            var grid = FindResultsGrid(window)!;
            var capped = grid.Columns[0].ActualWidth;
            // Without this the drag assertion below would also pass on a column
            // the cap was never in the way of, proving nothing.
            await Assert.That(capped).IsEqualTo(560).Within(1);

            Drag(window, FindHeader(window, 0)!, by: 120);

            await Assert.That(grid.Columns[0].ActualWidth).IsGreaterThan(capped + 100);

            window.Close();
        });
    }

    /// <summary>
    /// The grid is window-central and rebuilds its columns from scratch every
    /// time the active tab changes, so a drag only survives because the width is
    /// handed back to the tab it was made on (QueryViewModel.ColumnWidths).
    /// </summary>
    [Test]
    public async Task A_resized_column_keeps_its_width_across_a_tab_switch()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var grid = FindResultsGrid(window)!;
            var original = vm.ActiveTab;
            Drag(window, FindHeader(window, 0)!, by: 60);
            var resized = grid.Columns[0].ActualWidth;

            vm.AddTabCommand.Execute(null);
            Ui.Settle();
            vm.ActiveTab = original;
            Ui.Settle();

            await Assert.That(grid.Columns[0].ActualWidth).IsEqualTo(resized).Within(1);

            window.Close();
        });
    }

    /// <summary>
    /// The grid shows long values capped and folded onto one line (that is what
    /// keeps a jsonb-heavy table scrolling — see <c>CellText</c>), and the inline
    /// editor is pre-filled from exactly that display text. So a cell showing a
    /// preview must never open one: committing an untouched editor would write
    /// the preview over the real value. It opens the cell inspector instead,
    /// which carries the whole thing.
    /// </summary>
    [Test]
    public async Task A_previewed_cell_opens_the_inspector_instead_of_an_inline_editor()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var payload = new string('x', CellText.PreviewLength * 3);
            SeedEditableCell(vm, payload);

            var grid = FindResultsGrid(window)!;
            BeginEditingTheCell(grid);

            await Assert.That(vm.CellInspector.IsOpen).IsTrue();
            // The whole value, not the preview the cell was showing.
            await Assert.That(vm.CellInspector.DisplayText).IsEqualTo(payload);
            await Assert.That(EditorTextBox(window)).IsNull();

            window.Close();
        });
    }

    /// <summary>
    /// The other half of the pair: a value the cell is showing in full still
    /// edits inline. Without this, the guard above would also pass if inline
    /// editing had simply stopped working.
    /// </summary>
    [Test]
    public async Task A_cell_showing_its_whole_value_still_edits_inline()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            SeedEditableCell(vm, "short enough to read");

            var grid = FindResultsGrid(window)!;
            BeginEditingTheCell(grid);

            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            await Assert.That(EditorTextBox(window)?.Text).IsEqualTo("short enough to read");

            window.Close();
        });
    }

    // One editable row: a keyed table context is what makes the grid writable,
    // which is the only state in which BeginningEdit fires at all.
    private static void SeedEditableCell(MainViewModel vm, string payload)
    {
        var tab = vm.ActiveTab;
        tab.SeedResult(
            [new ColumnInfo("id", "bigint", typeof(long)), new ColumnInfo("payload", "text", typeof(string))],
            [[1L, payload]]);
        tab.EditContext = new EditableTableContext(
            "public", "t", ["id"],
            [new ColumnDetail("id", "bigint", NotNull: true, IsPrimaryKey: true),
             new ColumnDetail("payload", "text", NotNull: false, IsPrimaryKey: false)]);
        Ui.Settle();
    }

    private static void BeginEditingTheCell(DataGrid grid)
    {
        grid.SelectedIndex = 0;
        grid.CurrentColumn = grid.Columns[1];
        Ui.Settle();
        grid.BeginEdit();
        Ui.Settle();
    }

    private static TextBox? EditorTextBox(Window window) =>
        window.GetVisualDescendants()
            .OfType<DataGridCell>()
            .SelectMany(cell => cell.GetVisualDescendants().OfType<TextBox>())
            .FirstOrDefault();

    // A resize drag on a header's right edge: press inside the grip strip, move,
    // release. Sent as real pointer input so the DataGrid's own resize handling
    // runs — the point of these two tests is the interaction, not the property.
    private static void Drag(Window window, DataGridColumnHeader header, double by)
    {
        var grip = header.TranslatePoint(
            new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;

        window.MouseDown(grip, MouseButton.Left);
        Ui.Settle();
        window.MouseMove(grip.WithX(grip.X + by));
        Ui.Settle();
        window.MouseUp(grip.WithX(grip.X + by), MouseButton.Left);
        Ui.Settle();
    }

    // Headers carry their column index on the content this panel built for them
    // (ResultsGridPanel.CreateColumnHeader), which is also how the panel maps a
    // press back to a column.
    private static DataGridColumnHeader? FindHeader(Window window, int index) =>
        window.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .FirstOrDefault(header => header.Content is Control { Tag: int tag } && tag == index);

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
