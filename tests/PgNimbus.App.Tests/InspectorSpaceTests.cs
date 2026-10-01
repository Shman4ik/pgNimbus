using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.Converters;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Space opens the cell inspector on View, always. Only an explicit edit
/// gesture (a double-click on an editable json cell, F2 on a previewed one)
/// opens it on Edit. A release pass once saw it open on Edit "after Space";
/// what reproduces is the click before it: the DataGrid begins an edit when
/// the already-current cell is clicked again, slowly, and for a json or
/// previewed cell that edit was turned into the inspector's Edit tab. Every
/// test here drives real pointer and key input.
/// </summary>
public class InspectorSpaceTests
{
    private const string ShortJson = """{"a": 1}""";

    // Longer than CellText.PreviewLength, so the grid only previews it.
    private static readonly string LongJson =
        "{\"items\": [" + string.Join(", ", Enumerable.Range(0, 60).Select(i => $"\"item-{i}\"")) + "]}";

    [Test]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task Space_always_opens_the_inspector_on_view(bool editable, bool longValue)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            Seed(vm, longValue ? LongJson : ShortJson, editable);
            Click(window, Cell(window, 0, 1), 0.3);

            Ui.Press(window, CommandId.InspectCell);

            await AssertOpenOnView(vm, editable);
            window.Close();
        });
    }

    /// <summary>
    /// The report itself: a cell that is already current, clicked again (to put
    /// focus back in the grid, say), then Space. The click alone used to open
    /// the inspector on Edit for a previewed value, and a one-line inline editor
    /// for a short json one, before Space was ever pressed.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_second_single_click_on_a_json_cell_does_not_start_editing(bool longValue)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var value = longValue ? LongJson : ShortJson;
            await Assert.That(CellText.IsShortened(value)).IsEqualTo(longValue);
            Seed(vm, value, editable: true);
            var cell = Cell(window, 0, 1);

            Click(window, cell, 0.3);
            // Away from the first point, so it counts as a click of its own,
            // not the second half of a double-click.
            Click(window, cell, 0.7);

            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            await Assert.That(HasInlineEditor(window)).IsFalse();

            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable: true);
            window.Close();
        });
    }

    [Test]
    public async Task Space_after_the_about_overlay_opens_on_view()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            Seed(vm, LongJson, editable: true);
            var cell = Cell(window, 0, 1);
            Click(window, cell, 0.3);

            vm.ShowAboutCommand.Execute(null);
            Ui.Settle();
            await Assert.That(vm.IsAboutOpen).IsTrue();
            Ui.Press(window, Key.Escape);
            await Assert.That(vm.IsAboutOpen).IsFalse();

            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable: true);

            // And with a click to bring focus back first, which is what
            // someone does when Space seems to do nothing.
            vm.CellInspector.CloseCommand.Execute(null);
            Ui.Settle();
            Click(window, cell, 0.7);
            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable: true);
            window.Close();
        });
    }

    /// <summary>
    /// Edit, then close, then Space again: the inspector is one view model
    /// reused for every cell, so nothing from the last opening may carry over.
    /// Closed each way the card closes by pointer. (Not Escape: while the
    /// editor has focus AvaloniaEdit's text area keeps that key.)
    /// </summary>
    [Test]
    [Arguments(InspectorClose.CloseButton)]
    [Arguments(InspectorClose.Scrim)]
    [Arguments(InspectorClose.CancelThenCloseButton)]
    public async Task Space_after_editing_and_closing_opens_on_view(InspectorClose how)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            Seed(vm, LongJson, editable: true);
            var cell = Cell(window, 0, 1);
            Click(window, cell, 0.3);

            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable: true);
            ClickButton(window, "Edit");
            await Assert.That(vm.CellInspector.IsEditing).IsTrue();
            Close(window, how);
            await Assert.That(vm.CellInspector.IsOpen).IsFalse();

            // Space straight away (it does nothing if focus stayed in the
            // closed card), then again after a click that puts focus back.
            Ui.Press(window, CommandId.InspectCell);
            if (vm.CellInspector.IsOpen)
            {
                await AssertOpenOnView(vm, editable: true);
                Close(window, InspectorClose.CloseButton);
            }

            Click(window, cell, 0.7);
            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable: true);
            window.Close();
        });
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Space_after_a_double_click_opens_on_view(bool editable)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            Seed(vm, LongJson, editable);
            var cell = Cell(window, 0, 1);

            DoubleClick(window, cell);
            await Assert.That(vm.CellInspector.IsOpen).IsTrue();
            // A double-click edits an editable json cell; a read-only one is
            // only ever viewed.
            await Assert.That(vm.CellInspector.IsEditing).IsEqualTo(editable);
            Close(window, InspectorClose.CloseButton);

            Click(window, cell, 0.7);
            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable);
            window.Close();
        });
    }

    private static async Task AssertOpenOnView(MainViewModel vm, bool editable)
    {
        await Assert.That(vm.CellInspector.IsOpen).IsTrue();
        await Assert.That(vm.CellInspector.CanEdit).IsEqualTo(editable);
        await Assert.That(vm.CellInspector.IsEditing).IsFalse();
    }

    public enum InspectorClose
    {
        CloseButton,
        Scrim,
        CancelThenCloseButton,
    }

    private static void Close(Window window, InspectorClose how)
    {
        switch (how)
        {
            case InspectorClose.CloseButton:
                ClickButton(window, "✕");
                break;
            case InspectorClose.CancelThenCloseButton:
                ClickButton(window, "Cancel");
                ClickButton(window, "✕");
                break;
            case InspectorClose.Scrim:
                // The scrim's corner, outside the card's 40px margin.
                var scrim = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "CellInspectorOverlay");
                var corner = scrim.TranslatePoint(new Point(10, 10), window)!.Value;
                window.MouseDown(corner, MouseButton.Left);
                window.MouseUp(corner, MouseButton.Left);
                Ui.Settle();
                break;
        }
    }

    // A real click on one of the inspector card's buttons, found by its label.
    private static void ClickButton(Window window, string label)
    {
        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "CellInspectorCard");
        var button = card.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content as string == label && b.IsEffectivelyVisible);
        Click(window, button, 0.5);
    }

    private static void Seed(MainViewModel vm, string json, bool editable)
    {
        var tab = vm.ActiveTab;
        tab.SeedResult(
            [new ColumnInfo("id", "bigint", typeof(long)), new ColumnInfo("payload", "jsonb", typeof(string))],
            [[1L, json], [2L, json]]);
        tab.EditContext = editable
            ? new EditableTableContext(
                "public", "t", ["id"],
                [new ColumnDetail("id", "bigint", NotNull: true, IsPrimaryKey: true),
                 new ColumnDetail("payload", "jsonb", NotNull: false, IsPrimaryKey: false) { Editor = ColumnValueEditor.Json }])
            : null;
        Ui.Settle();
    }

    private static DataGridCell Cell(Window window, int row, int column)
    {
        var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
        var gridRow = window.GetVisualDescendants().OfType<DataGridRow>().First(r => r.Index == row);
        return grid.Columns[column].GetCellContent(gridRow)!.FindAncestorOfType<DataGridCell>()!;
    }

    private static Point PointIn(Window window, Control target, double fraction) =>
        target.TranslatePoint(new Point(target.Bounds.Width * fraction, target.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control target, double fraction)
    {
        var point = PointIn(window, target, fraction);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Ui.Settle();
    }

    private static void DoubleClick(Window window, Control target)
    {
        var point = PointIn(window, target, 0.5);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Ui.Settle();
    }

    private static bool HasInlineEditor(Window window) =>
        window.GetVisualDescendants().OfType<DataGridCell>()
            .SelectMany(cell => cell.GetVisualDescendants().OfType<TextBox>())
            .Any();
}
