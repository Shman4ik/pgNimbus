using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

/// <summary>
/// The "Schemas" sidebar tab: the catalog TreeView with its per-node context
/// menus, plus drag-a-node-into-the-editor and double-tap-to-browse. Binds to a
/// <see cref="SchemaTreeViewModel"/> supplied as its DataContext by the host
/// window. Purely visual interaction lives here (drag arming, dialog owner
/// resolution); the actual window-level actions (open tab, refresh caches,
/// build the Alter Table VM) are the sub-ViewModel's host-supplied callbacks,
/// so this panel never reaches back into MainWindow (UI design rule 7).
/// </summary>
public partial class SchemaTreePanel : UserControl
{
    public SchemaTreePanel()
    {
        InitializeComponent();

        // TreeViewItem's own DoubleTapped handling (toggling expand) marks the
        // event Handled, so it never reaches a plain `+=` subscription on the
        // parent TreeView. Use AddHandler with handledEventsToo to still see it.
        SchemaTreeView.AddHandler(InputElement.DoubleTappedEvent, OnSchemaTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        // Drag a schema/table/column out of the tree and drop it into the SQL
        // editor as a properly quoted identifier. The drag arms on press and
        // only starts after a small movement threshold, so plain clicks,
        // expander toggles, and double-click previews all behave as before.
        SchemaTreeView.AddHandler(InputElement.PointerPressedEvent, OnSchemaTreePointerPressed, RoutingStrategies.Tunnel);
        SchemaTreeView.AddHandler(InputElement.PointerMovedEvent, OnSchemaTreePointerMoved, RoutingStrategies.Tunnel);
        SchemaTreeView.AddHandler(InputElement.PointerReleasedEvent, (_, _) => _treeDragCandidate = null, RoutingStrategies.Tunnel);
    }

    private SchemaTreeViewModel? Model => DataContext as SchemaTreeViewModel;

    // --- Schema-tree drag & drop into the editor --------------------------

    // Armed on press over a draggable node; the drag itself starts only after
    // the pointer moves past a threshold with the button still down. The press
    // args are kept because DoDragDropAsync can only start from them.
    private (Point Origin, string Text, PointerPressedEventArgs PressArgs)? _treeDragCandidate;
    private const double DragThreshold = 4;

    /// <summary>The SQL identifier a tree node drops as, quoted only where a bare name wouldn't round-trip.</summary>
    private static string? DragTextFor(object? node) => node switch
    {
        ColumnNode column => SqlIdentifier.QuoteIfNeeded(column.Name),
        TableNode table => $"{SqlIdentifier.QuoteIfNeeded(table.Schema)}.{SqlIdentifier.QuoteIfNeeded(table.Name)}",
        SchemaNode schema => SqlIdentifier.QuoteIfNeeded(schema.Name),
        _ => null,
    };

    private void OnSchemaTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SchemaTreeView).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var node = (e.Source as Visual)?.DataContext;
        _treeDragCandidate = DragTextFor(node) is { } text
            ? (e.GetPosition(SchemaTreeView), text, e)
            : null;
    }

    private async void OnSchemaTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_treeDragCandidate is not { } candidate)
        {
            return;
        }

        if (!e.GetCurrentPoint(SchemaTreeView).Properties.IsLeftButtonPressed)
        {
            _treeDragCandidate = null;
            return;
        }

        var position = e.GetPosition(SchemaTreeView);
        if (Math.Abs(position.X - candidate.Origin.X) < DragThreshold &&
            Math.Abs(position.Y - candidate.Origin.Y) < DragThreshold)
        {
            return;
        }

        _treeDragCandidate = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(candidate.Text));
        await DragDrop.DoDragDropAsync(candidate.PressArgs, data, DragDropEffects.Copy);
    }

    // --- Context-menu actions ---------------------------------------------

    private void OnAlterTableClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TableNode table } || Model?.AlterTableViewModelFactory is not { } factory)
        {
            return;
        }

        var alterTableViewModel = factory(table);
        // Same TableNode instance the schema tree displays, so reloading its
        // children in place picks up the ALTER TABLE without a full tree refresh.
        alterTableViewModel.SchemaChanged += () => _ = table.RefreshAsync();

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            var dialog = new AlterTableDialog { DataContext = alterTableViewModel };
            dialog.ShowDialog(owner);
        }
    }

    // "Source (DDL)" - reconstructs the object's CREATE definition and opens it
    // in a new query tab.
    private async void OnShowSourceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: TableNode table } && Model?.ShowTableSourceRequested is { } showSource)
        {
            await showSource(table);
        }
    }

    private async void OnShowFunctionSourceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: FunctionNode function } && Model?.ShowFunctionSourceRequested is { } showSource)
        {
            await showSource(function);
        }
    }

    private async void OnInstallExtensionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ExtensionNode extension } && Model?.SetExtensionInstalledRequested is { } setInstalled)
        {
            await setInstalled(extension, true);
        }
    }

    private async void OnDropExtensionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ExtensionNode extension } || Model?.SetExtensionInstalledRequested is not { } setInstalled)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var confirm = new ConfirmDialog($"Drop extension \"{extension.Name}\"? Objects it provides will be removed.", "Drop");
        if (await confirm.ShowDialog<bool>(owner))
        {
            await setInstalled(extension, false);
        }
    }

    private void OnSchemaTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Read the node off the tapped TreeViewItem's DataContext rather than
        // SchemaTreeView.SelectedItem: on the very first click of a row that
        // wasn't already selected, SelectedItem can still be stale/null at
        // the point this handler runs.
        var container = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (Model is null)
        {
            return;
        }

        switch (container?.DataContext)
        {
            case TableNode table when Model.PreviewTableRequested is { } preview:
                _ = preview(table);
                break;
            // A function's natural default action is its source - same as the
            // context menu's "Source (DDL)".
            case FunctionNode { HasSource: true } function when Model.ShowFunctionSourceRequested is { } showSource:
                _ = showSource(function);
                break;
        }
    }
}
