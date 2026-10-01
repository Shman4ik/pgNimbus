using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.Views;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;
using static PgNimbus.App.Tests.InspectorUi;

namespace PgNimbus.App.Tests;

/// <summary>
/// Escape closes the cell inspector from wherever focus is inside it, and asks
/// first when that would throw away an edit. It used to do nothing while the
/// editor had focus: AvaloniaEdit's text area marks Escape handled, so the
/// window's own close never saw the key. Every test sends real keys.
/// </summary>
public class InspectorEscapeTests
{
    private const string Json = """{"a": 1}""";

    [Test]
    public async Task Escape_in_the_editor_with_nothing_changed_closes_the_inspector()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = OpenOnEdit();

            Ui.Press(window, Key.Escape);

            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            await Assert.That(Confirms(window)).IsEmpty();
            window.Close();
        });
    }

    [Test]
    public async Task Escape_in_the_viewer_closes_the_inspector()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            Seed(vm, Json, editable: true);
            Click(window, Cell(window, 0, 1), 0.3);
            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable: true);

            // Focus into the viewer's text, as a click to select some of it does.
            var viewer = InspectorViewer(window);
            Click(window, viewer, 0.2);
            await Assert.That(FocusIsIn(window, viewer)).IsTrue();

            Ui.Press(window, Key.Escape);

            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            window.Close();
        });
    }

    /// <summary>
    /// The edit is kept through the question: Escape on the confirm (its Cancel)
    /// goes back to the editor with the text as typed, and only Discard closes.
    /// </summary>
    [Test]
    public async Task Escape_with_unsaved_edits_asks_before_closing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = OpenOnEdit();
            Ui.Type(window, "x");
            var edited = vm.CellInspector.EditText;
            await Assert.That(vm.CellInspector.HasUnsavedEdit).IsTrue();

            Ui.Press(window, Key.Escape);
            var confirm = Confirms(window).Single();
            await Assert.That(confirm.FindControl<TextBlock>("MessageText")!.Text)
                .IsEqualTo("Discard your changes to \"payload\"?");
            await Assert.That(vm.CellInspector.IsOpen).IsTrue();

            // Escape again answers the question with Cancel: keep editing.
            Ui.Press(confirm, Key.Escape);
            await Assert.That(Confirms(window)).IsEmpty();
            await Assert.That(vm.CellInspector.IsOpen).IsTrue();
            await Assert.That(vm.CellInspector.IsEditing).IsTrue();
            await Assert.That(vm.CellInspector.EditText).IsEqualTo(edited);
            await Assert.That(FocusIsIn(window, InspectorEditor(window))).IsTrue();

            Ui.Press(window, Key.Escape);
            confirm = Confirms(window).Single();
            var discard = confirm.FindControl<Button>("ConfirmButton")!;
            await Assert.That((string?)discard.Content).IsEqualTo("Discard");
            await Assert.That(discard.IsDefault).IsFalse();
            Click(confirm, discard, 0.5);

            await Assert.That(Confirms(window)).IsEmpty();
            await Assert.That(vm.CellInspector.IsOpen).IsFalse();

            // Nothing of the discarded edit comes back with the next opening.
            Click(window, Cell(window, 0, 1), 0.7);
            Ui.Press(window, CommandId.InspectCell);
            await AssertOpenOnView(vm, editable: true);
            ClickButton(window, "Edit");
            await Assert.That(vm.CellInspector.EditText).IsEqualTo(vm.CellInspector.DisplayText);
            await Assert.That(vm.CellInspector.HasUnsavedEdit).IsFalse();
            window.Close();
        });
    }

    /// <summary>The edit buffer survives a hop to View, so closing from there asks too.</summary>
    [Test]
    public async Task Escape_on_view_with_an_edit_kept_from_the_editor_asks()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = OpenOnEdit();
            Ui.Type(window, "x");
            ClickButton(window, "View");
            await Assert.That(vm.CellInspector.IsEditing).IsFalse();

            Ui.Press(window, Key.Escape);

            await Assert.That(Confirms(window).Count).IsEqualTo(1);
            await Assert.That(vm.CellInspector.IsOpen).IsTrue();
            Confirms(window).Single().Close(false);
            Ui.Settle();
            window.Close();
        });
    }

    [Test]
    [Arguments(InspectorClose.CloseButton)]
    [Arguments(InspectorClose.Scrim)]
    public async Task A_pointer_close_with_unsaved_edits_asks_too(InspectorClose how)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = OpenOnEdit();
            Ui.Type(window, "x");
            var edited = vm.CellInspector.EditText;

            Close(window, how);
            var confirm = Confirms(window).Single();
            Click(confirm, confirm.FindControl<Button>("CancelButton")!, 0.5);

            await Assert.That(Confirms(window)).IsEmpty();
            await Assert.That(vm.CellInspector.IsOpen).IsTrue();
            await Assert.That(vm.CellInspector.EditText).IsEqualTo(edited);
            window.Close();
        });
    }

    /// <summary>
    /// The editor's find bar is closer to focus than the card: Escape there
    /// closes the find bar, and the next one closes the inspector.
    /// </summary>
    [Test]
    public async Task Escape_in_the_find_bar_closes_only_the_find_bar()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = OpenOnEdit();
            Ui.Press(window, CommandId.Find);
            var panel = InspectorEditor(window).SearchPanel!;
            await Assert.That(panel.IsClosed).IsFalse();

            Ui.Press(window, Key.Escape);

            await Assert.That(panel.IsClosed).IsTrue();
            await Assert.That(vm.CellInspector.IsOpen).IsTrue();

            Ui.Press(window, Key.Escape);
            await Assert.That(vm.CellInspector.IsOpen).IsFalse();
            window.Close();
        });
    }

    // An editable json cell, double-clicked: the inspector opens on Edit with
    // the editor focused, which is where the reported Escape went nowhere.
    private static (Window Window, ViewModels.MainViewModel Vm) OpenOnEdit()
    {
        var (window, vm) = Scenarios.Shell();
        Ui.Show(window);
        Seed(vm, Json, editable: true);
        DoubleClick(window, Cell(window, 0, 1));
        Ui.Settle();
        if (!vm.CellInspector.IsEditing || !FocusIsIn(window, InspectorEditor(window)))
        {
            throw new InvalidOperationException("The inspector did not open on Edit with the editor focused.");
        }

        return (window, vm);
    }

    private static bool FocusIsIn(Window window, Control control) =>
        window.FocusManager?.GetFocusedElement() is Visual focused
        && (ReferenceEquals(focused, control) || focused.GetVisualAncestors().Contains(control));

    private static List<ConfirmDialog> Confirms(Window window) =>
        window.OwnedWindows.OfType<ConfirmDialog>().Where(w => w.IsVisible).ToList();
}
