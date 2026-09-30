using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Editing;

namespace PgNimbus.App.Platform;

/// <summary>What a macOS text-editing key does, independent of the control it lands in.</summary>
public enum MacTextAction
{
    None,

    /// <summary>⌥⌫ — delete the word before the caret.</summary>
    DeleteWordBackward,

    /// <summary>⌥⌦ (fn+⌥⌫) — delete the word after the caret.</summary>
    DeleteWordForward,

    /// <summary>⌘⌫ — delete to the start of the line (at the start: join with the line above).</summary>
    DeleteToLineStart,

    /// <summary>⌘⌦ (fn+⌘⌫) — delete to the end of the line (at the end: join with the line below).</summary>
    DeleteToLineEnd,

    /// <summary>⌘↑ (⇧ selects) — the start of the document.</summary>
    DocumentStart,

    /// <summary>⌘↓ (⇧ selects) — the end of the document.</summary>
    DocumentEnd,

    /// <summary>⌃A (⇧ selects) — the start of the line.</summary>
    LineStart,

    /// <summary>⌃E (⇧ selects) — the end of the line.</summary>
    LineEnd,

    /// <summary>⌃K — cut to the end of the line into the kill buffer (at the end: the line break).</summary>
    KillToLineEnd,

    /// <summary>⌃Y — insert what the last ⌃K took.</summary>
    Yank,

    /// <summary>⌃D — delete the character after the caret.</summary>
    DeleteForward,

    /// <summary>⌃H — delete the character before the caret.</summary>
    DeleteBackward,

    /// <summary>⌃F — one character right.</summary>
    CharForward,

    /// <summary>⌃B — one character left.</summary>
    CharBackward,

    /// <summary>⌃N — one line down (multi-line only; in an open completion list, the next row).</summary>
    LineDown,

    /// <summary>⌃P — one line up (multi-line only; in an open completion list, the previous row).</summary>
    LineUp,

    /// <summary>⌃T — swap the characters around the caret.</summary>
    Transpose,
}

/// <summary>A classified key: the action, and whether ⇧ extends the selection.</summary>
public readonly record struct MacTextGesture(MacTextAction Action, bool Extend = false)
{
    public static readonly MacTextGesture None = new(MacTextAction.None);
}

/// <summary>
/// The macOS text-editing keys — the Cocoa text system's defaults (⌥⌫, ⌘⌫, ⌘↑,
/// ⌃A/⌃E/⌃K …) that every NSTextView answers and that Avalonia's TextBox and
/// AvaloniaEdit's TextArea only partly do. Measured on a real Mac: the SQL
/// editor ignored ⌥⌫, ⌘⌫, ⌘↑/⌘↓ and every ⌃ key; the TextBoxes ignored ⌘⌫ and
/// ⌃A/⌃E.
/// <para>
/// These are <b>not</b> catalog commands (UI design rule 5's platform-text
/// exception): they are how text behaves on the platform, not things pgNimbus
/// does, so they have no palette row, no cheat-sheet line and no window binding.
/// What keeps them from shadowing a command is <see cref="Classify"/> matching
/// exact modifiers, and a test that checks every catalog chord against it.
/// </para>
/// <para>
/// Wired once, as tunnelled class handlers on <see cref="TextBox"/> and
/// <see cref="TextArea"/>, so every text box and every editor in the app gets
/// them — the SQL editor, the cell inspector's JSON editor, the find box, a grid
/// cell's editor — without any view opting in. Class handlers run before an
/// element's own handlers, so the SQL editor's key handler and an open
/// completion list see only what these leave alone.
/// </para>
/// <para>
/// Active on macOS under the Cmd scheme only: with the Windows scheme chosen on
/// a Mac, Ctrl is the command key and ⌃A is Select All, so the whole set steps
/// aside. Every edit is one undo step.
/// </para>
/// </summary>
public static class MacTextKeys
{
    /// <summary>
    /// Forces the keys on or off for a subtree (inherited), whatever the
    /// platform — the seam the headless tests use to drive the macOS path on
    /// any OS without touching the process-wide hotkey scheme. Unset (null)
    /// means "decide from the platform".
    /// </summary>
    public static readonly AttachedProperty<bool?> ForceProperty =
        AvaloniaProperty.RegisterAttached<Control, bool?>("Force", typeof(MacTextKeys), inherits: true);

    public static bool? GetForce(AvaloniaObject target) => target.GetValue(ForceProperty);

    public static void SetForce(AvaloniaObject target, bool? value) => target.SetValue(ForceProperty, value);

    private static bool _installed;

    // What the last ⌃K took; ⌃Y puts it back. One buffer for the whole app, as
    // Cocoa keeps one per process.
    private static string _killBuffer = string.Empty;

    /// <summary>Registers the class handlers. Idempotent; called from <c>App.Initialize</c>.</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnTextBoxKeyDown, RoutingStrategies.Tunnel);
        InputElement.KeyDownEvent.AddClassHandler<TextArea>(OnTextAreaKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Whether the keys apply to <paramref name="control"/> right now.</summary>
    public static bool IsActive(Control control) =>
        control.GetValue(ForceProperty)
        ?? (OperatingSystem.IsMacOS() && Hotkeys.Command == KeyModifiers.Meta);

    /// <summary>
    /// The one table of gestures. Modifiers must match exactly, so ⌘⇧⌫ (Rollback)
    /// and ⌃Space (completion) never land here.
    /// </summary>
    public static MacTextGesture Classify(Key key, KeyModifiers modifiers)
    {
        const KeyModifiers Ctrl = KeyModifiers.Control;
        const KeyModifiers CtrlShift = KeyModifiers.Control | KeyModifiers.Shift;
        const KeyModifiers Cmd = KeyModifiers.Meta;
        const KeyModifiers CmdShift = KeyModifiers.Meta | KeyModifiers.Shift;
        const KeyModifiers Opt = KeyModifiers.Alt;

        return (key, modifiers) switch
        {
            (Key.Back, Opt) => new(MacTextAction.DeleteWordBackward),
            (Key.Delete, Opt) => new(MacTextAction.DeleteWordForward),
            (Key.Back, Cmd) => new(MacTextAction.DeleteToLineStart),
            (Key.Delete, Cmd) => new(MacTextAction.DeleteToLineEnd),
            (Key.Up, Cmd) => new(MacTextAction.DocumentStart),
            (Key.Up, CmdShift) => new(MacTextAction.DocumentStart, Extend: true),
            (Key.Down, Cmd) => new(MacTextAction.DocumentEnd),
            (Key.Down, CmdShift) => new(MacTextAction.DocumentEnd, Extend: true),
            (Key.A, Ctrl) => new(MacTextAction.LineStart),
            (Key.A, CtrlShift) => new(MacTextAction.LineStart, Extend: true),
            (Key.E, Ctrl) => new(MacTextAction.LineEnd),
            (Key.E, CtrlShift) => new(MacTextAction.LineEnd, Extend: true),
            (Key.K, Ctrl) => new(MacTextAction.KillToLineEnd),
            (Key.Y, Ctrl) => new(MacTextAction.Yank),
            (Key.D, Ctrl) => new(MacTextAction.DeleteForward),
            (Key.H, Ctrl) => new(MacTextAction.DeleteBackward),
            (Key.F, Ctrl) => new(MacTextAction.CharForward),
            (Key.B, Ctrl) => new(MacTextAction.CharBackward),
            (Key.N, Ctrl) => new(MacTextAction.LineDown),
            (Key.P, Ctrl) => new(MacTextAction.LineUp),
            (Key.T, Ctrl) => new(MacTextAction.Transpose),
            _ => MacTextGesture.None,
        };
    }

    // ------------------------------------------------------------ TextBox

    private static void OnTextBoxKeyDown(TextBox box, KeyEventArgs e)
    {
        // Tunnelling, a TextBox nested in another one would see its child's keys.
        if (e.Handled || !ReferenceEquals(e.Source, box) || !IsActive(box))
        {
            return;
        }

        var gesture = Classify(e.Key, e.KeyModifiers);
        if (gesture.Action == MacTextAction.None)
        {
            return;
        }

        e.Handled = HandleTextBox(box, gesture);
    }

    private static bool HandleTextBox(TextBox box, MacTextGesture gesture)
    {
        var text = box.Text ?? string.Empty;
        var caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        var multiline = box.AcceptsReturn;

        switch (gesture.Action)
        {
            // The TextBox's own ⌥⌫/⌥⌦ already work on macOS (its keymap knows
            // the Option word modifier); leave them to it.
            case MacTextAction.DeleteWordBackward or MacTextAction.DeleteWordForward:
                return false;

            // So do ⌘↑/⌘↓, and in a single-line box there is nothing to move
            // between; ⌃N/⌃P have no line to go to either.
            case MacTextAction.DocumentStart or MacTextAction.DocumentEnd:
                return false;

            case MacTextAction.LineDown or MacTextAction.LineUp:
                return multiline && Forward(box, gesture.Action == MacTextAction.LineDown ? Key.Down : Key.Up);

            case MacTextAction.LineStart:
                MoveTextBoxCaret(box, TextLineStart(text, caret), gesture.Extend);
                return true;

            case MacTextAction.LineEnd:
                MoveTextBoxCaret(box, TextLineEnd(text, caret), gesture.Extend);
                return true;

            case MacTextAction.CharForward:
                return Forward(box, Key.Right);

            case MacTextAction.CharBackward:
                return Forward(box, Key.Left);

            case MacTextAction.DeleteForward:
                return Forward(box, Key.Delete);

            case MacTextAction.DeleteBackward:
                return Forward(box, Key.Back);
        }

        // Everything below edits.
        if (box.IsReadOnly)
        {
            return true;
        }

        var (selStart, selEnd) = (Math.Min(box.SelectionStart, box.SelectionEnd), Math.Max(box.SelectionStart, box.SelectionEnd));
        var hasSelection = selStart != selEnd;

        switch (gesture.Action)
        {
            case MacTextAction.DeleteToLineStart:
                if (hasSelection)
                {
                    ReplaceTextBoxRange(box, selStart, selEnd, string.Empty);
                }
                else
                {
                    var start = TextLineStart(text, caret);
                    ReplaceTextBoxRange(box, start == caret ? PreviousBoundary(text, caret) : start, caret, string.Empty);
                }

                return true;

            case MacTextAction.DeleteToLineEnd:
                if (hasSelection)
                {
                    ReplaceTextBoxRange(box, selStart, selEnd, string.Empty);
                }
                else
                {
                    var end = TextLineEnd(text, caret);
                    ReplaceTextBoxRange(box, caret, end == caret ? NextBoundary(text, caret) : end, string.Empty);
                }

                return true;

            case MacTextAction.KillToLineEnd:
            {
                var (from, to) = hasSelection ? (selStart, selEnd) : KillRange(text, caret);
                if (to > from)
                {
                    _killBuffer = text[from..to];
                    ReplaceTextBoxRange(box, from, to, string.Empty);
                }

                return true;
            }

            case MacTextAction.Yank:
                if (_killBuffer.Length > 0)
                {
                    ReplaceTextBoxRange(box, hasSelection ? selStart : caret, hasSelection ? selEnd : caret, _killBuffer);
                }

                return true;

            case MacTextAction.Transpose:
                if (!hasSelection && TransposeRange(text, caret) is { } swap)
                {
                    ReplaceTextBoxRange(box, swap.Start, swap.Start + 2, swap.Swapped);
                    box.CaretIndex = swap.Start + 2;
                }

                return true;
        }

        return false;
    }

    // Re-raises the key the Emacs chord stands for (⌃F → →, ⌃D → ⌦ …) on the
    // box itself, so it gets the box's own handling — grapheme clusters, IME,
    // wrapped lines — rather than a second implementation of it.
    private static bool Forward(TextBox box, Key key)
    {
        var synthetic = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = box,
        };
        box.RaiseEvent(synthetic);
        return true;
    }

    private static void MoveTextBoxCaret(TextBox box, int target, bool extend)
    {
        if (!extend)
        {
            // Not ClearSelection(): that collapses onto SelectionStart, the anchor.
            box.SelectionStart = target;
            box.SelectionEnd = target;
            box.CaretIndex = target;
            return;
        }

        // Avalonia keeps the caret at SelectionEnd; SelectionStart is the anchor.
        var anchor = box.SelectionStart != box.SelectionEnd ? box.SelectionStart : box.CaretIndex;
        box.CaretIndex = target;
        box.SelectionStart = anchor;
        box.SelectionEnd = target;
    }

    // One undo step: select the range and replace it the way typing or a
    // Backspace over a selection would, which is what the box's undo tracks.
    private static void ReplaceTextBoxRange(TextBox box, int start, int end, string replacement)
    {
        if (end < start || (end == start && replacement.Length == 0))
        {
            return;
        }

        box.SelectionStart = start;
        box.SelectionEnd = end;
        box.SelectedText = replacement;
    }

    // ------------------------------------------------------------ TextArea

    private static void OnTextAreaKeyDown(TextArea area, KeyEventArgs e)
    {
        // The find panel's box lives inside the TextArea: its keys tunnel
        // through here first, and are the TextBox handler's to answer.
        if (e.Handled || !ReferenceEquals(e.Source, area) || area.Document is null || !IsActive(area))
        {
            return;
        }

        var gesture = Classify(e.Key, e.KeyModifiers);
        if (gesture.Action == MacTextAction.None)
        {
            return;
        }

        e.Handled = HandleTextArea(area, gesture);
    }

    private static bool HandleTextArea(TextArea area, MacTextGesture gesture)
    {
        var document = area.Document;
        var caret = area.Caret.Offset;
        var line = document.GetLineByOffset(caret);

        switch (gesture.Action)
        {
            case MacTextAction.DeleteWordBackward:
                return Run(area, AvaloniaEdit.EditingCommands.DeletePreviousWord);
            case MacTextAction.DeleteBackward:
                return Run(area, AvaloniaEdit.EditingCommands.Backspace);
            case MacTextAction.DeleteForward:
                return Run(area, AvaloniaEdit.EditingCommands.Delete);
            case MacTextAction.CharForward:
                return Run(area, AvaloniaEdit.EditingCommands.MoveRightByCharacter);
            case MacTextAction.CharBackward:
                return Run(area, AvaloniaEdit.EditingCommands.MoveLeftByCharacter);
            case MacTextAction.DocumentStart:
                return Run(area, gesture.Extend
                    ? AvaloniaEdit.EditingCommands.SelectToDocumentStart
                    : AvaloniaEdit.EditingCommands.MoveToDocumentStart);
            case MacTextAction.DocumentEnd:
                return Run(area, gesture.Extend
                    ? AvaloniaEdit.EditingCommands.SelectToDocumentEnd
                    : AvaloniaEdit.EditingCommands.MoveToDocumentEnd);

            case MacTextAction.LineDown or MacTextAction.LineUp:
            {
                var down = gesture.Action == MacTextAction.LineDown;
                // An open completion list is a stacked input handler: there ⌃N/⌃P
                // walk the rows, as the arrows do.
                if (!area.StackedInputHandlers.IsEmpty)
                {
                    var synthetic = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = down ? Key.Down : Key.Up };
                    foreach (var handler in area.StackedInputHandlers)
                    {
                        handler.OnPreviewKeyDown(synthetic);
                        if (synthetic.Handled)
                        {
                            return true;
                        }
                    }
                }

                return Run(area, down ? AvaloniaEdit.EditingCommands.MoveDownByLine : AvaloniaEdit.EditingCommands.MoveUpByLine);
            }

            case MacTextAction.LineStart:
                MoveTextAreaCaret(area, line.Offset, gesture.Extend);
                return true;

            case MacTextAction.LineEnd:
                MoveTextAreaCaret(area, line.EndOffset, gesture.Extend);
                return true;
        }

        // Everything below edits. A read-only editor (the inspector's View tab)
        // still swallows the key rather than let it fall through to something else.
        if (!area.ReadOnlySectionProvider.CanInsert(caret))
        {
            return true;
        }

        var selection = area.Selection;
        var hasSelection = !selection.IsEmpty;

        switch (gesture.Action)
        {
            // Not AvaloniaEdit's DeleteNextWord: that deletes to the next word's
            // start (the Windows reading, taking the space after the word too);
            // Cocoa deletes to the end of the word ahead.
            case MacTextAction.DeleteWordForward:
                if (hasSelection)
                {
                    return Run(area, AvaloniaEdit.EditingCommands.Delete);
                }

                DeleteTextAreaRange(area, caret, WordEndAfter(document.Text, caret));
                return true;

            case MacTextAction.DeleteToLineStart:
                if (hasSelection || caret == line.Offset)
                {
                    // A selection goes as a whole; at the start of a line the
                    // line break goes, joining it to the one above.
                    return Run(area, AvaloniaEdit.EditingCommands.Backspace);
                }

                DeleteTextAreaRange(area, line.Offset, caret);
                return true;

            case MacTextAction.DeleteToLineEnd:
                if (hasSelection || caret == line.EndOffset)
                {
                    return Run(area, AvaloniaEdit.EditingCommands.Delete);
                }

                DeleteTextAreaRange(area, caret, line.EndOffset);
                return true;

            case MacTextAction.KillToLineEnd:
            {
                var (from, to) = hasSelection
                    ? (selection.SurroundingSegment.Offset, selection.SurroundingSegment.EndOffset)
                    : caret == line.EndOffset
                        ? (caret, caret + line.DelimiterLength)
                        : (caret, line.EndOffset);
                if (to > from)
                {
                    _killBuffer = document.GetText(from, to - from);
                    DeleteTextAreaRange(area, from, to);
                }

                return true;
            }

            case MacTextAction.Yank:
                if (_killBuffer.Length > 0)
                {
                    using (document.RunUpdate())
                    {
                        area.Selection.ReplaceSelectionWithText(_killBuffer);
                    }

                    area.Caret.BringCaretToView();
                }

                return true;

            case MacTextAction.Transpose:
                if (!hasSelection && TransposeRange(document.Text, caret, line.EndOffset) is { } swap)
                {
                    using (document.RunUpdate())
                    {
                        document.Replace(swap.Start, 2, swap.Swapped);
                    }

                    area.ClearSelection();
                    area.Caret.Offset = swap.Start + 2;
                    area.Caret.BringCaretToView();
                }

                return true;
        }

        return false;
    }

    // AvaloniaEdit's own commands already know read-only sections, virtual
    // space, rectangular selections and the caret's desired column.
    private static bool Run(TextArea area, AvaloniaEdit.RoutedCommand command)
    {
        command.Execute(null, area);
        area.Caret.BringCaretToView();
        return true;
    }

    private static void MoveTextAreaCaret(TextArea area, int target, bool extend)
    {
        var old = area.Caret.Position;
        if (!extend)
        {
            area.ClearSelection();
        }

        area.Caret.Offset = target;
        if (extend)
        {
            area.Selection = area.Selection.StartSelectionOrSetEndpoint(old, area.Caret.Position);
        }

        area.Caret.DesiredXPos = double.NaN;
        area.Caret.BringCaretToView();
    }

    // Selecting the range and deleting it through the editor's own Delete
    // keeps read-only sections honoured and makes it one undo step.
    private static void DeleteTextAreaRange(TextArea area, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        area.Selection = Selection.Create(area, start, end);
        Run(area, AvaloniaEdit.EditingCommands.Delete);
    }

    // ------------------------------------------------------------ text helpers

    /// <summary>The start of the logical line holding <paramref name="caret"/> (after the previous break).</summary>
    public static int TextLineStart(string text, int caret)
    {
        var i = Math.Clamp(caret, 0, text.Length);
        while (i > 0 && text[i - 1] is not ('\n' or '\r'))
        {
            i--;
        }

        return i;
    }

    /// <summary>The end of the logical line holding <paramref name="caret"/> (before its break).</summary>
    public static int TextLineEnd(string text, int caret)
    {
        var i = Math.Clamp(caret, 0, text.Length);
        while (i < text.Length && text[i] is not ('\n' or '\r'))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Where ⌥⌦ stops: past any whitespace (line breaks included), then to the end
    /// of the word there — or past one punctuation character when no word follows.
    /// </summary>
    public static int WordEndAfter(string text, int caret)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        var i = Math.Clamp(caret, 0, text.Length);
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i < text.Length && IsWord(text[i]))
        {
            while (i < text.Length && IsWord(text[i]))
            {
                i++;
            }
        }
        else if (i < text.Length)
        {
            i = NextBoundary(text, i);
        }

        return i;
    }

    // One character back, a CRLF or a surrogate pair counting as one.
    private static int PreviousBoundary(string text, int caret)
    {
        if (caret <= 0)
        {
            return 0;
        }

        if (caret >= 2 && (text[caret - 2], text[caret - 1]) is ('\r', '\n')
            || caret >= 2 && char.IsSurrogatePair(text[caret - 2], text[caret - 1]))
        {
            return caret - 2;
        }

        return caret - 1;
    }

    private static int NextBoundary(string text, int caret)
    {
        if (caret >= text.Length)
        {
            return text.Length;
        }

        if (caret + 1 < text.Length && ((text[caret], text[caret + 1]) is ('\r', '\n')
            || char.IsSurrogatePair(text[caret], text[caret + 1])))
        {
            return caret + 2;
        }

        return caret + 1;
    }

    // ⌃K: to the end of the line, or, at the end, the line break itself.
    private static (int From, int To) KillRange(string text, int caret)
    {
        var end = TextLineEnd(text, caret);
        return end > caret ? (caret, end) : (caret, NextBoundary(text, caret));
    }

    /// <summary>
    /// ⌃T, as Cocoa does it: swap the characters either side of the caret and
    /// step past them; at the end of a line, swap the two before it. Null where
    /// there is nothing to swap (fewer than two characters, a line break or a
    /// surrogate half in the way).
    /// </summary>
    public static (int Start, string Swapped)? TransposeRange(string text, int caret, int? lineEnd = null)
    {
        var end = lineEnd ?? TextLineEnd(text, caret);
        var start = caret >= end ? caret - 2 : caret - 1;
        if (start < 0 || start + 2 > text.Length)
        {
            return null;
        }

        var (a, b) = (text[start], text[start + 1]);
        if (a is '\n' or '\r' || b is '\n' or '\r' || char.IsSurrogate(a) || char.IsSurrogate(b))
        {
            return null;
        }

        return (start, new string([b, a]));
    }
}
