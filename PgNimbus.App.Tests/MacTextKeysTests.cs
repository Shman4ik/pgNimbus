using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using PgNimbus.App.Completion;
using PgNimbus.App.Platform;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The macOS text-editing keys (<see cref="MacTextKeys"/>) in the real SQL
/// editor and a real TextBox, driven with key input. The macOS path is forced
/// per window through <see cref="MacTextKeys.ForceProperty"/>, so these run on
/// every OS and never touch the process-wide Ctrl/Cmd scheme other tests read.
/// </summary>
public class MacTextKeysTests
{
    public const KeyModifiers Ctrl = KeyModifiers.Control;
    public const KeyModifiers Cmd = KeyModifiers.Meta;
    public const KeyModifiers Opt = KeyModifiers.Alt;
    public const KeyModifiers Shift = KeyModifiers.Shift;

    private static (Window Window, ViewModels.MainViewModel Vm, TextEditor Editor) OpenEditor(string marked, bool mac = true)
    {
        var (window, vm) = Scenarios.Shell();
        vm.CompletionProvider.Load(new CompletionCatalog(
            ["public"],
            [
                new CompletionTable("public", "orders", [new TableColumn("orders", "id", "int4"), new TableColumn("orders", "customer_id", "int4")]),
                new CompletionTable("public", "customers", [new TableColumn("customers", "id", "int4"), new TableColumn("customers", "name", "text")]),
            ],
            [],
            [],
            ["public"]));
        MacTextKeys.SetForce(window, mac);
        Ui.Show(window);

        var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
        var caret = marked.IndexOf('|');
        editor.Text = marked.Remove(caret, 1);
        editor.TextArea.Focus();
        editor.CaretOffset = caret;
        Ui.Settle();
        return (window, vm, editor);
    }

    private static string Marked(TextEditor editor) =>
        editor.Text.ReplaceLineEndings("\n").Insert(editor.CaretOffset - CrBefore(editor.Text, editor.CaretOffset), "|");

    private static int CrBefore(string text, int offset) => text[..offset].Count(c => c == '\r');

    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    // --- The editor -------------------------------------------------------

    [Test]
    [Arguments("SELECT foo_bar|", Key.Back, Opt, "SELECT |")]
    [Arguments("SELECT |foo_bar FROM t", Key.Delete, Opt, "SELECT | FROM t")]
    [Arguments("SELECT| foo_bar FROM t", Key.Delete, Opt, "SELECT| FROM t")]
    [Arguments("SELECT a,|\n  b", Key.Delete, Opt, "SELECT a,|")]
    [Arguments("SELECT a,\n  b, c|", Key.Back, Cmd, "SELECT a,\n|")]
    [Arguments("SELECT a,\n|b", Key.Back, Cmd, "SELECT a,|b")]
    [Arguments("SELECT |a, b\nFROM t", Key.Delete, Cmd, "SELECT |\nFROM t")]
    [Arguments("SELECT a|\nFROM t", Key.Delete, Cmd, "SELECT a|FROM t")]
    [Arguments("SELECT a,\n  b|, c\nFROM t", Key.Up, Cmd, "|SELECT a,\n  b, c\nFROM t")]
    [Arguments("SELECT a,\n  b|, c\nFROM t", Key.Down, Cmd, "SELECT a,\n  b, c\nFROM t|")]
    [Arguments("SELECT a,\n  b|, c\nFROM t", Key.A, Ctrl, "SELECT a,\n|  b, c\nFROM t")]
    [Arguments("SELECT a,\n  b|, c\nFROM t", Key.E, Ctrl, "SELECT a,\n  b, c|\nFROM t")]
    [Arguments("SELECT a,\n  b|, c\nFROM t", Key.K, Ctrl, "SELECT a,\n  b|\nFROM t")]
    [Arguments("SELECT a,|\nb", Key.K, Ctrl, "SELECT a,|b")]
    [Arguments("SEL|ECT", Key.D, Ctrl, "SEL|CT")]
    [Arguments("SEL|ECT", Key.H, Ctrl, "SE|ECT")]
    [Arguments("SEL|ECT", Key.F, Ctrl, "SELE|CT")]
    [Arguments("SEL|ECT", Key.B, Ctrl, "SE|LECT")]
    [Arguments("SELECT a,\n  b|, c", Key.P, Ctrl, "SEL|ECT a,\n  b, c")]
    [Arguments("SEL|ECT a,\n  b, c", Key.N, Ctrl, "SELECT a,\n  b|, c")]
    [Arguments("SELET|C", Key.T, Ctrl, "SELECT|")]
    [Arguments("SELETC|", Key.T, Ctrl, "SELECT|")]
    public async Task A_mac_text_key_edits_the_sql_editor(string before, Key key, KeyModifiers modifiers, string after)
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor(before);

            Ui.Press(window, key, modifiers);

            await Assert.That(Marked(editor)).IsEqualTo(after);
            window.Close();
        });
    }

    [Test]
    [Arguments(Key.Back, Opt)]
    [Arguments(Key.Back, Cmd)]
    [Arguments(Key.Delete, Cmd)]
    [Arguments(Key.K, Ctrl)]
    [Arguments(Key.T, Ctrl)]
    public async Task Each_edit_is_one_undo_step(Key key, KeyModifiers modifiers)
    {
        await Ui.Run(async () =>
        {
            const string text = "SELECT alpha_beta,\n  gamma delta, epsilon\nFROM t";
            var (window, _, editor) = OpenEditor(text.Insert(text.IndexOf("delta", StringComparison.Ordinal) + 2, "|"));

            Ui.Press(window, key, modifiers);
            await Assert.That(Lf(editor.Text)).IsNotEqualTo(text);

            editor.Undo();
            Ui.Settle();
            await Assert.That(Lf(editor.Text)).IsEqualTo(text);
            window.Close();
        });
    }

    [Test]
    public async Task Shift_extends_the_selection_to_the_line_and_the_document_ends()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT a,\n  b|, c\nFROM t");

            Ui.Press(window, Key.E, Ctrl | Shift);
            await Assert.That(editor.SelectedText).IsEqualTo(", c");

            Ui.Press(window, Key.A, Ctrl | Shift);
            await Assert.That(editor.SelectedText).IsEqualTo("  b");

            Ui.Press(window, Key.Down, Cmd | Shift);
            await Assert.That(Lf(editor.SelectedText)).IsEqualTo(", c\nFROM t");
            window.Close();
        });
    }

    [Test]
    public async Task Ctrl_k_then_ctrl_y_puts_the_line_back()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT |a, b\nFROM t");

            Ui.Press(window, Key.K, Ctrl);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT |\nFROM t");

            Ui.Press(window, Key.Y, Ctrl);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT a, b|\nFROM t");
            window.Close();
        });
    }

    [Test]
    public async Task Cmd_backspace_takes_a_selection_as_a_whole()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT abc FROM t|");
            editor.Select(7, 3);
            Ui.Settle();

            Ui.Press(window, Key.Back, Cmd);

            await Assert.That(editor.Text).IsEqualTo("SELECT  FROM t");
            window.Close();
        });
    }

    [Test]
    public async Task A_read_only_editor_keeps_its_text_and_still_moves()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT a,\n  b|, c");
            editor.IsReadOnly = true;

            Ui.Press(window, Key.K, Ctrl);
            Ui.Press(window, Key.Back, Cmd);
            await Assert.That(Lf(editor.Text)).IsEqualTo("SELECT a,\n  b, c");

            Ui.Press(window, Key.A, Ctrl);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT a,\n|  b, c");
            window.Close();
        });
    }

    [Test]
    public async Task Off_the_mac_path_the_keys_are_left_alone()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT a,\n  b|, c", mac: false);

            Ui.Press(window, Key.K, Ctrl);
            Ui.Press(window, Key.Back, Cmd);
            Ui.Press(window, Key.T, Ctrl);

            await Assert.That(Lf(editor.Text)).IsEqualTo("SELECT a,\n  b, c");
            window.Close();
        });
    }

    [Test]
    public async Task Ctrl_n_and_ctrl_p_walk_an_open_completion_list()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT * FROM |");
            Ui.Press(window, CommandId.Completion);
            var list = window.GetVisualDescendants().OfType<CompletionListBox>().Single(l => l.IsEffectivelyVisible);
            var first = list.SelectedIndex;

            Ui.Press(window, Key.N, Ctrl);
            await Assert.That(list.SelectedIndex).IsEqualTo(first + 1);
            Ui.Press(window, Key.P, Ctrl);
            await Assert.That(list.SelectedIndex).IsEqualTo(first);

            // …and the text and caret stayed where they were.
            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM |");
            window.Close();
        });
    }

    [Test]
    public async Task Cmd_up_with_the_completion_list_open_moves_the_caret_not_the_list()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT * FROM |");
            Ui.Press(window, CommandId.Completion);

            Ui.Press(window, Key.Up, Cmd);

            await Assert.That(editor.CaretOffset).IsEqualTo(0);
            window.Close();
        });
    }

    [Test]
    public async Task The_find_box_inside_the_editor_answers_its_own_keys()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = OpenEditor("SELECT abc FROM t|");
            Ui.Press(window, CommandId.Find);
            var box = window.GetVisualDescendants().OfType<AvaloniaEdit.Search.SearchPanel>().Single()
                .GetVisualDescendants().OfType<TextBox>().First(b => b.IsEffectivelyVisible);
            box.Focus();
            Ui.Type(window, "abc");

            Ui.Press(window, Key.Back, Cmd);

            await Assert.That(box.Text ?? string.Empty).IsEqualTo(string.Empty);
            await Assert.That(editor.Text).IsEqualTo("SELECT abc FROM t");
            window.Close();
        });
    }

    [Test]
    public async Task The_cell_inspectors_json_editor_takes_the_keys_too()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, _) = OpenEditor("|");
            vm.CellInspector.Open("metadata", """{"a": 1}""", 0, canEdit: true, commit: (_, _) => Task.FromResult<string?>(null), validatesAsJson: true, startEditing: true);
            Ui.Settle();
            var json = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "JsonInspectorEditor" && e.IsEffectivelyVisible);
            json.TextArea.Focus();
            json.CaretOffset = json.Text.Length;
            Ui.Settle();

            Ui.Press(window, Key.Up, Cmd);
            await Assert.That(json.CaretOffset).IsEqualTo(0);

            Ui.Press(window, Key.K, Ctrl);
            await Assert.That(Lf(json.Text)).DoesNotContain("{");
            window.Close();
        });
    }

    // --- A TextBox --------------------------------------------------------

    private static (Window Window, TextBox Box) OpenTextBox(string marked, bool multiline = false)
    {
        var box = new TextBox { AcceptsReturn = multiline, Width = 300 };
        var window = new Window { Width = 400, Height = 200, Content = box };
        MacTextKeys.SetForce(window, true);
        Ui.Show(window);

        var caret = marked.IndexOf('|');
        box.Text = marked.Remove(caret, 1);
        box.Focus();
        box.CaretIndex = caret;
        Ui.Settle();
        return (window, box);
    }

    private static string Marked(TextBox box) => (box.Text ?? string.Empty).Insert(box.CaretIndex, "|");

    [Test]
    [Arguments("orders cust|omer", Key.Back, Cmd, "|omer")]
    [Arguments("orders cust|omer", Key.Delete, Cmd, "orders cust|")]
    [Arguments("orders cust|omer", Key.A, Ctrl, "|orders customer")]
    [Arguments("orders cust|omer", Key.E, Ctrl, "orders customer|")]
    [Arguments("orders cust|omer", Key.K, Ctrl, "orders cust|")]
    [Arguments("orders cust|omer", Key.D, Ctrl, "orders cust|mer")]
    [Arguments("orders cust|omer", Key.H, Ctrl, "orders cus|omer")]
    [Arguments("orders cust|omer", Key.F, Ctrl, "orders custo|mer")]
    [Arguments("orders cust|omer", Key.B, Ctrl, "orders cus|tomer")]
    [Arguments("orders cut|somer", Key.T, Ctrl, "orders cust|omer")]
    public async Task A_mac_text_key_edits_a_text_box(string before, Key key, KeyModifiers modifiers, string after)
    {
        await Ui.Run(async () =>
        {
            var (window, box) = OpenTextBox(before);

            Ui.Press(window, key, modifiers);

            await Assert.That(Marked(box)).IsEqualTo(after);
            window.Close();
        });
    }

    [Test]
    public async Task A_text_box_edit_is_one_undo_step()
    {
        await Ui.Run(async () =>
        {
            var (window, box) = OpenTextBox("orders cust|omer");

            Ui.Press(window, Key.Back, Cmd);
            await Assert.That(box.Text).IsEqualTo("omer");

            box.Undo();
            Ui.Settle();
            await Assert.That(box.Text).IsEqualTo("orders customer");
            window.Close();
        });
    }

    [Test]
    public async Task Ctrl_e_with_shift_selects_to_the_end_of_a_text_box()
    {
        await Ui.Run(async () =>
        {
            var (window, box) = OpenTextBox("orders cust|omer");

            Ui.Press(window, Key.E, Ctrl | Shift);

            await Assert.That(box.SelectedText).IsEqualTo("omer");
            window.Close();
        });
    }

    [Test]
    public async Task Ctrl_a_and_ctrl_e_stay_on_the_line_of_a_multi_line_box()
    {
        await Ui.Run(async () =>
        {
            var (window, box) = OpenTextBox("first\nsec|ond\nthird", multiline: true);

            Ui.Press(window, Key.A, Ctrl);
            await Assert.That(Marked(box)).IsEqualTo("first\n|second\nthird");

            Ui.Press(window, Key.E, Ctrl);
            await Assert.That(Marked(box)).IsEqualTo("first\nsecond|\nthird");
            window.Close();
        });
    }

    [Test]
    public async Task The_sidebar_filter_takes_cmd_backspace()
    {
        await Ui.Run(async () =>
        {
            var (window, _, _) = OpenEditor("|");
            var box = window.GetVisualDescendants().OfType<TextBox>()
                .First(b => b.PlaceholderText?.StartsWith("Filter schemas", StringComparison.Ordinal) == true);
            box.Focus();
            Ui.Type(window, "orders");

            Ui.Press(window, Key.Back, Cmd);

            await Assert.That(box.Text ?? string.Empty).IsEqualTo(string.Empty);
            window.Close();
        });
    }

    // --- The table itself -------------------------------------------------

    [Test]
    public async Task No_command_chord_on_the_cmd_scheme_is_a_text_key()
    {
        // The catalog under the Cmd scheme: Command is ⌘, Control a literal ⌃.
        var clashes = new List<string>();
        foreach (var descriptor in CommandCatalog.All)
        {
            foreach (var chord in new[] { descriptor.Chord, descriptor.AltChord })
            {
                if (chord is not { } c)
                {
                    continue;
                }

                var modifiers = KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Command) ? KeyModifiers.Meta : KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Control) ? KeyModifiers.Control : KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Shift) ? KeyModifiers.Shift : KeyModifiers.None;
                modifiers |= c.Modifiers.HasFlag(ChordModifiers.Alt) ? KeyModifiers.Alt : KeyModifiers.None;

                if (MacTextKeys.Classify(CommandBindings.ToKey(c.Key), modifiers).Action != MacTextAction.None)
                {
                    clashes.Add($"{descriptor.Id} ({c.Label("Cmd")})");
                }
            }
        }

        await Assert.That(clashes).IsEmpty();
    }

    [Test]
    [Arguments(Key.Back, KeyModifiers.Meta | KeyModifiers.Shift)] // Rollback transaction
    [Arguments(Key.Space, KeyModifiers.Control)] // completion
    [Arguments(Key.Space, KeyModifiers.Control | KeyModifiers.Shift)] // argument hints
    [Arguments(Key.A, KeyModifiers.Meta)] // select all
    [Arguments(Key.Left, KeyModifiers.Meta)] // line start: AvaloniaEdit's own
    public async Task Classify_leaves_other_chords_alone(Key key, KeyModifiers modifiers)
    {
        await Assert.That(MacTextKeys.Classify(key, modifiers).Action).IsEqualTo(MacTextAction.None);
    }
}
