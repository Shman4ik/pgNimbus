using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace PgNimbus.App.Platform;

/// <summary>
/// Makes typing in an AvaloniaEdit editor undo a word at a time, the way
/// TextEdit and VS Code do, instead of a character at a time.
/// <para>
/// Measured (<c>EditorTypingUndoTests</c>): AvaloniaEdit gives every
/// <c>TextInput</c> event its own document update and so its own undo group, so
/// typing <c>SELECT abc</c> and pressing ⌘Z once took back the <c>c</c> alone —
/// ten presses to undo ten keystrokes. Its <see cref="UndoStack"/> can continue
/// a closed group (<see cref="UndoStack.StartContinuedUndoGroup"/>) but nothing
/// in the editor asks it to.
/// </para>
/// <para>
/// This does, for every <see cref="TextArea"/> in the app, from one pair of
/// class hooks: a tunnelled <c>TextInput</c> handler opens an undo group before
/// the editor inserts, continuing the previous one when the keystroke extends
/// the same run, and the event's route-finished notification closes it. A run
/// is a word plus the spaces typed after it; a punctuation character always
/// starts a new one (so a completion accepted on <c>(</c>, and an auto-closed
/// bracket, stay their own step), and so does anything that is not plain
/// typing at the caret — a selection being replaced, a caret that moved, an
/// undo, a paste, a completion accept or any other edit in between, all of
/// which leave a different group descriptor behind.
/// </para>
/// </summary>
public static class EditorTypingUndo
{
    private static bool _installed;

    // The group opened for the TextInput event being routed, closed when its route finishes.
    private static (RoutedEventArgs Args, UndoStack Undo, TypingRun Run, TextArea Area)? _open;

    /// <summary>
    /// The group descriptor of a typing run: which editor and document it was
    /// typed into, and where its caret ended. A later keystroke continues the
    /// run only if the stack's last group is still this one and the caret is
    /// still where the run left it.
    /// </summary>
    private sealed class TypingRun(TextArea area, TextDocument document)
    {
        public TextArea Area { get; } = area;

        public TextDocument Document { get; } = document;

        public int EndOffset { get; set; } = -1;
    }

    /// <summary>Registers the hooks. Idempotent; called from <c>App.Initialize</c>.</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        InputElement.TextInputEvent.AddClassHandler<TextArea>(OnTextInputTunnel, RoutingStrategies.Tunnel);
        InputElement.TextInputEvent.RouteFinished.Subscribe(new Observer(OnRouteFinished));
    }

    /// <summary>
    /// Whether typing <paramref name="next"/> right after <paramref name="previous"/>
    /// extends the same undo step: letters/digits/underscores join a word, spaces
    /// and tabs join whatever came before them on the line, and any other character
    /// starts a step of its own.
    /// </summary>
    public static bool JoinsRun(char previous, char next)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        if (IsWord(next))
        {
            return IsWord(previous);
        }

        if (next is ' ' or '\t')
        {
            return previous is not ('\n' or '\r');
        }

        return false;
    }

    private static void OnTextInputTunnel(TextArea area, TextInputEventArgs e)
    {
        CloseOpenGroup();

        if (e.Handled || !ReferenceEquals(e.Source, area) || area.Document is not { } document
            || string.IsNullOrEmpty(e.Text) || document.IsInUpdate)
        {
            return;
        }

        var undo = document.UndoStack;
        var run = new TypingRun(area, document);
        if (Continues(area, document, undo, e.Text))
        {
            undo.StartContinuedUndoGroup(run);
        }
        else
        {
            undo.StartUndoGroup(run);
        }

        _open = (e, undo, run, area);
    }

    private static bool Continues(TextArea area, TextDocument document, UndoStack undo, string text)
    {
        if (undo.LastGroupDescriptor is not TypingRun previous
            || !ReferenceEquals(previous.Area, area) || !ReferenceEquals(previous.Document, document)
            || !area.Selection.IsEmpty || area.OverstrikeMode)
        {
            return false;
        }

        var caret = area.Caret.Offset;
        var single = text.Length == 1 || (text.Length == 2 && char.IsSurrogatePair(text[0], text[1]));
        return single && caret > 0 && previous.EndOffset == caret
            && JoinsRun(document.GetCharAt(caret - 1), text[0]);
    }

    private static void OnRouteFinished(RoutedEventArgs e)
    {
        // TextInput routes tunnel then bubble; the group spans both.
        if (_open is { } open && ReferenceEquals(open.Args, e) && e.Route == RoutingStrategies.Bubble)
        {
            CloseOpenGroup();
        }
    }

    private static void CloseOpenGroup()
    {
        if (_open is not { } open)
        {
            return;
        }

        _open = null;
        open.Run.EndOffset = ReferenceEquals(open.Area.Document, open.Run.Document) ? open.Area.Caret.Offset : -1;
        open.Undo.EndUndoGroup();
    }

    private sealed class Observer(Action<RoutedEventArgs> onNext) : IObserver<RoutedEventArgs>
    {
        public void OnNext(RoutedEventArgs value) => onNext(value);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
