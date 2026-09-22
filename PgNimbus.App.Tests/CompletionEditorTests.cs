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
                new CompletionTable("public", "customers", [new TableColumn("customers", "id", "int4"), new TableColumn("customers", "name", "text"), new TableColumn("customers", "Order Id", "int4")]),
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

    // Enter writes the platform newline; the assertions don't care which.
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

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

    // --- T29: accept next to text that is already there ---

    [Test]
    public async Task A_function_accepted_before_its_paren_reuses_it()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT coal|(x, 0)");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT coalesce(|x, 0)");
            window.Close();
        });
    }

    [Test]
    public async Task A_table_accepted_before_a_typed_alias_keeps_that_alias()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, editor) = Open("SELECT * FROM ord| x WHERE x.id = 1");
            if (!vm.AutoAliasTables)
            {
                vm.ToggleAutoAliasCommand.Execute(null);
            }

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.orders| x WHERE x.id = 1");
            window.Close();
        });
    }

    [Test]
    public async Task Accepting_inside_a_quoted_identifier_writes_one_pair_of_quotes()
    {
        await Ui.Run(async () =>
        {
            // The closing quote is the auto-closed one the editor wrote.
            var (window, _, editor) = Open("SELECT * FROM public.customers c WHERE \"Ord|\" = 1");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.customers c WHERE \"Order Id\"| = 1");
            window.Close();
        });
    }

    // --- T30: redo puts the whole accept back, alias included ---

    [Test]
    public async Task Redo_restores_the_table_and_its_alias_together()
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
            editor.Undo();
            Ui.Settle();
            editor.Redo();
            Ui.Settle();

            await Assert.That(editor.Text).IsEqualTo("SELECT * FROM public.orders o");
            await Assert.That(editor.CanRedo).IsFalse();
            window.Close();
        });
    }

    // --- T32: pasted text and tab switches never accept anything ---

    [Test]
    public async Task Pasted_text_does_not_open_the_popup()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE |");

            // A paste / IME commit arrives as one multi-character text input.
            Ui.Type(window, "o.id = 1");
            Ui.Press(window, Key.Enter);

            await Assert.That(Lf(editor.Text)).IsEqualTo("SELECT * FROM public.orders o WHERE o.id = 1\n");
            window.Close();
        });
    }

    [Test]
    public async Task Pasting_over_an_open_popup_does_not_accept_into_the_paste()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE o.|");

            Ui.Type(window, "c"); // popup opens on the columns of o
            Ui.Type(window, "ustomer_id = 42");
            Ui.Press(window, Key.Enter);

            await Assert.That(Lf(editor.Text)).IsEqualTo("SELECT * FROM public.orders o WHERE o.customer_id = 42\n");
            window.Close();
        });
    }

    [Test]
    public async Task Switching_tabs_closes_the_popup_before_it_can_accept()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, editor) = Open("SELECT * FROM public.customers c WHERE c.na|");
            var first = vm.ActiveTab;

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, CommandId.NewTab);
            await Assert.That(vm.ActiveTab).IsNotEqualTo(first);
            editor.TextArea.Focus();
            var before = editor.Text;
            editor.CaretOffset = before.Length;
            Ui.Press(window, Key.Enter);

            await Assert.That(Lf(editor.Text)).IsEqualTo(Lf(before) + "\n");
            await Assert.That(first.Sql).IsEqualTo("SELECT * FROM public.customers c WHERE c.na");
            window.Close();
        });
    }

    // --- Package F: a derived table's output through the real popup (T15) ---

    [Test]
    public async Task A_derived_tables_output_column_completes_after_its_alias()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT q.c| FROM (SELECT customer_id AS cid FROM public.orders) q");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT q.cid| FROM (SELECT customer_id AS cid FROM public.orders) q");
            window.Close();
        });
    }

    // --- Package G: JOIN USING through the real popup (T22) ---

    [Test]
    public async Task Join_using_accepts_the_one_column_both_sides_share()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o JOIN public.customers c USING (|)");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.orders o JOIN public.customers c USING (id|)");
            window.Close();
        });
    }
}
