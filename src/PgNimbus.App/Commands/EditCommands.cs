using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.Core.Commands;

namespace PgNimbus.App;

/// <summary>The standard Edit-menu verbs.</summary>
public enum EditCommand
{
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    SelectAll,
    Find,
}

/// <summary>
/// A view that answers an Edit-menu verb itself, because what the verb means
/// there is not what a text control would do: the results grid copies its
/// selected cells as TSV, and Find in a browsed grid opens a filter. Returning
/// false passes the verb on to the next ancestor.
/// </summary>
public interface IEditCommandTarget
{
    bool TryExecute(EditCommand command);
}

/// <summary>
/// Routes an Edit-menu verb to whatever holds keyboard focus.
/// <para>
/// This exists because of how the macOS menu bar works: a menu item's key
/// equivalent is matched by AppKit before the key reaches Avalonia, so once the
/// Edit menu carries Cmd+C, the text box or grid that used to receive Cmd+C as a
/// key press receives a menu click instead. Each verb therefore has to be
/// handed on to the focused control explicitly, or adding the menu would break
/// copy and paste everywhere.
/// </para>
/// <para>
/// The walk starts at the focused element and goes up the visual tree to the
/// window. The first thing on the way that can take the verb does: an
/// <see cref="IEditCommandTarget"/> (a panel with its own meaning for it), a
/// <see cref="TextBox"/> (which covers the command palette's box, the tab
/// search box, every dialog field and a grid cell being edited), or an
/// AvaloniaEdit <see cref="TextEditor"/> (the SQL editor and the cell
/// inspector's JSON editor). Find is never a text box's; it goes on up to the
/// panel or window that owns a search.
/// </para>
/// </summary>
public static class EditCommands
{
    /// <summary>
    /// The gesture a verb shows in the Edit menu. Undo, Copy and Find come from
    /// the command catalog, where they are already documented; the rest are the
    /// platform's own text-editing keys, spelled with the live Ctrl/Cmd scheme
    /// like every other menu gesture.
    /// </summary>
    public static KeyGesture GestureFor(EditCommand command)
    {
        var cmd = Hotkeys.Command;
        return command switch
        {
            EditCommand.Undo => CommandBindings.GestureFor(CommandId.UndoRedo) ?? new KeyGesture(Key.Z, cmd),
            EditCommand.Redo => new KeyGesture(Key.Z, cmd | KeyModifiers.Shift),
            EditCommand.Cut => new KeyGesture(Key.X, cmd),
            EditCommand.Copy => CommandBindings.GestureFor(CommandId.CopySelection) ?? new KeyGesture(Key.C, cmd),
            EditCommand.Paste => new KeyGesture(Key.V, cmd),
            EditCommand.SelectAll => new KeyGesture(Key.A, cmd),
            EditCommand.Find => CommandBindings.GestureFor(CommandId.Find) ?? new KeyGesture(Key.F, cmd),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    /// <summary>
    /// Performs <paramref name="command"/> on the focused element of
    /// <paramref name="topLevel"/>. Returns false when nothing on the way to the
    /// window took it (focus on a button, say), which is a no-op, not an error.
    /// </summary>
    public static bool Execute(TopLevel topLevel, EditCommand command)
    {
        var start = topLevel.FocusManager?.GetFocusedElement() as Visual ?? topLevel;
        for (var node = start; node is not null; node = node.GetVisualParent())
        {
            if (TryExecuteOn(node, command))
            {
                return true;
            }

            if (ReferenceEquals(node, topLevel))
            {
                break;
            }
        }

        return false;
    }

    private static bool TryExecuteOn(Visual node, EditCommand command)
    {
        if (node is IEditCommandTarget target && target.TryExecute(command))
        {
            return true;
        }

        switch (node)
        {
            case TextBox textBox when command != EditCommand.Find:
                Execute(textBox, command);
                return true;
            case TextEditor editor when command != EditCommand.Find:
                Execute(editor, command);
                return true;
            default:
                return false;
        }
    }

    private static void Execute(TextBox textBox, EditCommand command)
    {
        switch (command)
        {
            case EditCommand.Undo:
                textBox.Undo();
                break;
            case EditCommand.Redo:
                textBox.Redo();
                break;
            case EditCommand.Cut:
                textBox.Cut();
                break;
            case EditCommand.Copy:
                textBox.Copy();
                break;
            case EditCommand.Paste:
                textBox.Paste();
                break;
            case EditCommand.SelectAll:
                textBox.SelectAll();
                break;
        }
    }

    private static void Execute(TextEditor editor, EditCommand command)
    {
        switch (command)
        {
            case EditCommand.Undo:
                editor.Undo();
                break;
            case EditCommand.Redo:
                editor.Redo();
                break;
            case EditCommand.Cut:
                editor.Cut();
                break;
            case EditCommand.Copy:
                editor.Copy();
                break;
            case EditCommand.Paste:
                editor.Paste();
                break;
            case EditCommand.SelectAll:
                editor.SelectAll();
                break;
        }
    }
}
