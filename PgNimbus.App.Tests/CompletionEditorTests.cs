using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.Completion;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The completion popup in the real editor, driven with real keys: the
/// accept matrix of docs/design/sql-editing-experience.md (T28–T31) — what
/// Ctrl+Space filters on, what an accept replaces, and that one Undo takes a
/// whole accept back, auto-alias included.
/// </summary>
public class CompletionEditorTests
{
    private static (Avalonia.Controls.Window Window, ViewModels.MainViewModel Vm, TextEditor Editor) Open(string marked)
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
        Ui.Show(window);

        var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
        var caret = marked.IndexOf('|');
        editor.Text = marked.Remove(caret, 1);
        editor.TextArea.Focus();
        editor.CaretOffset = caret;
        Ui.Settle();
        return (window, vm, editor);
    }

    private static string Marked(TextEditor editor) => editor.Text.Insert(editor.CaretOffset, "|");

    [Test]
    public async Task Ctrl_space_after_a_prefix_filters_on_it_and_replaces_all_of_it()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("sel|");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT|");
            window.Close();
        });
    }

    [Test]
    public async Task Accepting_mid_word_replaces_the_word()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT cust|omer_idx FROM public.orders");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT customer_id| FROM public.orders");
            window.Close();
        });
    }

    [Test]
    public async Task A_table_and_its_alias_are_one_undo_step()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, editor) = Open("SELECT * FROM ord|");
            if (!vm.AutoAliasTables)
            {
                vm.ToggleAutoAliasCommand.Execute(null);
            }

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.orders o|");

            editor.Undo();
            Ui.Settle();
            await Assert.That(editor.Text).IsEqualTo("SELECT * FROM ord");
            window.Close();
        });
    }

    [Test]
    public async Task No_match_then_backspace_brings_the_popup_back()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE o.|");

            Ui.Type(window, "cust");
            Ui.Type(window, "q"); // "custq" matches nothing: the popup closes
            Ui.Press(window, Key.Back);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.orders o WHERE o.customer_id|");
            window.Close();
        });
    }

    [Test]
    public async Task A_second_ctrl_space_leaves_one_working_popup()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.customers c WHERE c.na|");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.customers c WHERE c.name|");
            window.Close();
        });
    }
}
