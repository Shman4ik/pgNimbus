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
using static PgNimbus.App.Tests.InspectorUi;

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
    /// Closed each way the card closes by pointer; Escape has its own tests
    /// (<see cref="InspectorEscapeTests"/>).
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
}
