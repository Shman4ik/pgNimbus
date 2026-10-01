using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Tests;

/// <summary>
/// Real pointer input on the results grid and the cell inspector card, shared
/// by <see cref="InspectorSpaceTests"/> and <see cref="InspectorEscapeTests"/>.
/// </summary>
public static class InspectorUi
{
    public enum InspectorClose
    {
        CloseButton,
        Scrim,
        CancelThenCloseButton,
    }

    /// <summary>A two-row result with a jsonb column, editable or not.</summary>
    public static void Seed(MainViewModel vm, string json, bool editable)
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

    public static async Task AssertOpenOnView(MainViewModel vm, bool editable)
    {
        await Assert.That(vm.CellInspector.IsOpen).IsTrue();
        await Assert.That(vm.CellInspector.CanEdit).IsEqualTo(editable);
        await Assert.That(vm.CellInspector.IsEditing).IsFalse();
    }

    public static void Close(Window window, InspectorClose how)
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

    /// <summary>A real click on one of the inspector card's buttons, found by its label.</summary>
    public static void ClickButton(Window window, string label)
    {
        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "CellInspectorCard");
        var button = card.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content as string == label && b.IsEffectivelyVisible);
        Click(window, button, 0.5);
    }

    /// <summary>The inspector's edit-mode editor (CellValueView's JsonInspectorEditor).</summary>
    public static TextEditor InspectorEditor(Window window) =>
        window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "JsonInspectorEditor");

    /// <summary>The inspector's read-only viewer (CellValueView's Viewer).</summary>
    public static TextEditor InspectorViewer(Window window) =>
        window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Viewer");

    public static DataGridCell Cell(Window window, int row, int column)
    {
        var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
        var gridRow = window.GetVisualDescendants().OfType<DataGridRow>().First(r => r.Index == row);
        return grid.Columns[column].GetCellContent(gridRow)!.FindAncestorOfType<DataGridCell>()!;
    }

    public static void Click(Window window, Control target, double fraction)
    {
        var point = PointIn(window, target, fraction);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Ui.Settle();
    }

    public static void DoubleClick(Window window, Control target)
    {
        var point = PointIn(window, target, 0.5);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Ui.Settle();
    }

    public static bool HasInlineEditor(Window window) =>
        window.GetVisualDescendants().OfType<DataGridCell>()
            .SelectMany(cell => cell.GetVisualDescendants().OfType<TextBox>())
            .Any();

    private static Point PointIn(Window window, Control target, double fraction) =>
        target.TranslatePoint(new Point(target.Bounds.Width * fraction, target.Bounds.Height / 2), window)!.Value;
}
