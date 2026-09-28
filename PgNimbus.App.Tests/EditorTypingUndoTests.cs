using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.Platform;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Undo granularity of typing in the SQL editor (<see cref="EditorTypingUndo"/>).
/// Each character arrives as its own TextInput event, as it does from a real
/// keyboard. Measured before the fix: one Undo took back one character.
/// </summary>
public class EditorTypingUndoTests
{
    private static (Window Window, TextEditor Editor) Open()
    {
        var (window, _) = Scenarios.Shell();
        Ui.Show(window);
        var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
        editor.Text = string.Empty;
        editor.TextArea.Focus();
        Ui.Settle();
        return (window, editor);
    }

    private static void TypeKeys(Window window, string text)
    {
        foreach (var c in text)
        {
            Ui.Type(window, c.ToString());
        }
    }

    private static void Undo(TextEditor editor)
    {
        editor.Undo();
        Ui.Settle();
    }

    [Test]
    public async Task One_undo_takes_back_a_typed_word_not_a_character()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = Open();
            TypeKeys(window, "select abc");
            // Whatever the popup did, it must not have taken anything.
            Ui.Press(window, Key.Escape);
            await Assert.That(editor.Text).IsEqualTo("select abc");

            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo("select ");

            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo(string.Empty);
            window.Close();
        });
    }

    [Test]
    public async Task Punctuation_is_a_step_of_its_own()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = Open();
            TypeKeys(window, "a.b");
            Ui.Press(window, Key.Escape);

            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo("a.");
            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo("a");
            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo(string.Empty);
            window.Close();
        });
    }

    [Test]
    public async Task Moving_the_caret_ends_the_run()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = Open();
            TypeKeys(window, "abc");
            Ui.Press(window, Key.Escape);
            Ui.Press(window, Key.Left);
            TypeKeys(window, "x");
            Ui.Press(window, Key.Escape);
            await Assert.That(editor.Text).IsEqualTo("abxc");

            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo("abc");
            window.Close();
        });
    }

    [Test]
    public async Task Typing_after_an_undo_starts_a_new_step()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = Open();
            TypeKeys(window, "abc ");
            TypeKeys(window, "def");
            Ui.Press(window, Key.Escape);
            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo("abc ");

            TypeKeys(window, "gh");
            Ui.Press(window, Key.Escape);
            Undo(editor);
            await Assert.That(editor.Text).IsEqualTo("abc ");
            window.Close();
        });
    }

    [Test]
    [Arguments('a', 'b', true)]
    [Arguments('_', 'b', true)]
    [Arguments('a', ' ', true)]
    [Arguments(' ', ' ', true)]
    [Arguments(' ', 'a', false)]
    [Arguments('a', '.', false)]
    [Arguments('.', 'a', false)]
    [Arguments('\n', ' ', false)]
    public async Task What_joins_a_run(char previous, char next, bool joins)
    {
        await Assert.That(EditorTypingUndo.JoinsRun(previous, next)).IsEqualTo(joins);
    }
}
