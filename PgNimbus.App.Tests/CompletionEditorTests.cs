using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Rendering;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
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

            // In the case it was typed in (F02, the default).
            await Assert.That(Marked(editor)).IsEqualTo("select|");
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
            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM orders o|");

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

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM orders| x WHERE x.id = 1");
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

            await Assert.That(editor.Text).IsEqualTo("SELECT * FROM orders o");
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

    // --- Package H: the argument hint and cast types through real keys ---

    private static QueryEditorPanel EditorPanel(Avalonia.Controls.Window window) =>
        window.GetVisualDescendants().OfType<QueryEditorPanel>().First();

    private static void TypeKeys(Avalonia.Controls.Window window, string text)
    {
        foreach (var c in text)
        {
            Ui.Type(window, c.ToString());
        }
    }

    [Test]
    public async Task Typing_an_open_paren_after_a_function_shows_its_signature_and_escape_hides_it()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, _) = Open("SELECT |");
            vm.CompletionProvider.Load(new CompletionCatalog(["public"], [], [], [], ["public"])
            {
                BuiltinFunctions = [new CompletionFunction("pg_catalog", new FunctionInfo("round", "numeric, integer", "numeric", 'f'))],
            });
            var panel = EditorPanel(window);

            TypeKeys(window, "round(");
            await Assert.That(panel.SignatureHintText).IsEqualTo("round(numeric, integer) → numeric");

            Ui.Press(window, Key.Escape);
            await Assert.That(panel.SignatureHintText).IsNull();
            window.Close();
        });
    }

    [Test]
    public async Task The_signature_hint_closes_when_the_caret_leaves_the_call()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, editor) = Open("SELECT |");
            vm.CompletionProvider.Load(new CompletionCatalog(["public"], [], [], [], ["public"])
            {
                BuiltinFunctions = [new CompletionFunction("pg_catalog", new FunctionInfo("round", "numeric, integer", "numeric", 'f'))],
            });
            var panel = EditorPanel(window);

            TypeKeys(window, "round(1, 2");
            await Assert.That(panel.SignatureHintText).IsNotNull();

            // Step over the auto-closed ")": the caret is outside the call now.
            Ui.Press(window, Key.End);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT round(1, 2)|");
            await Assert.That(panel.SignatureHintText).IsNull();
            window.Close();
        });
    }

    // Where the hint landed, in window coordinates, against the text view's own.
    private static (Rect Hint, Rect View, Rect Line) SignatureHintBounds(Avalonia.Controls.Window window, TextEditor editor)
    {
        var hint = EditorPanel(window).GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("signatureHint"))
            ?? window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("signatureHint"));
        var view = editor.TextArea.TextView;
        var line = view.GetVisualPosition(new TextViewPosition(editor.TextArea.Caret.Line, 1), VisualYPosition.LineTop) - view.ScrollOffset;
        var lineBottom = view.GetVisualPosition(new TextViewPosition(editor.TextArea.Caret.Line, 1), VisualYPosition.LineBottom) - view.ScrollOffset;
        Rect InWindow(Visual v, Rect r) => new(v.TranslatePoint(r.TopLeft, window)!.Value, r.Size);
        return (InWindow(hint, new Rect(hint.Bounds.Size)), InWindow(view, new Rect(view.Bounds.Size)),
            InWindow(view, new Rect(0, line.Y, view.Bounds.Width, lineBottom.Y - line.Y)));
    }

    private static Avalonia.Controls.Window OpenWithRound(string marked, out TextEditor editor)
    {
        var (window, vm, e) = Open(marked);
        vm.CompletionProvider.Load(new CompletionCatalog(["public"], [], [], [], ["public"])
        {
            BuiltinFunctions = [new CompletionFunction("pg_catalog", new FunctionInfo("round", "numeric, integer", "numeric", 'f'))],
        });
        editor = e;
        return window;
    }

    [Test]
    public async Task A_signature_hint_on_the_first_line_opens_below_it_inside_the_editor()
    {
        await Ui.Run(async () =>
        {
            var window = OpenWithRound("SELECT |", out var editor);

            TypeKeys(window, "round(");
            Ui.Settle();
            var (hint, view, line) = SignatureHintBounds(window, editor);

            // Nothing above line 1 belongs to the editor: the hint used to cover the toolbar there.
            await Assert.That(hint.Top).IsGreaterThanOrEqualTo(view.Top);
            await Assert.That(hint.Top).IsGreaterThanOrEqualTo(line.Bottom);
            window.Close();
        });
    }

    [Test]
    public async Task A_signature_hint_with_room_above_still_opens_above_its_line()
    {
        await Ui.Run(async () =>
        {
            var window = OpenWithRound("SELECT 1;\n\n\n\nSELECT |", out var editor);

            TypeKeys(window, "round(");
            Ui.Settle();
            var (hint, view, line) = SignatureHintBounds(window, editor);

            await Assert.That(hint.Bottom).IsLessThanOrEqualTo(line.Top);
            await Assert.That(hint.Top).IsGreaterThanOrEqualTo(view.Top);
            window.Close();
        });
    }

    [Test]
    public async Task A_double_colon_opens_the_type_list()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT '1'|");

            TypeKeys(window, "::int");
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT '1'::integer|");
            window.Close();
        });
    }

    // --- Package I: long scripts are read off the UI thread ---

    // Past the 50k-character threshold where completion reads the text on the thread pool.
    private static readonly string LongScript =
        string.Concat(Enumerable.Repeat("SELECT id, name FROM public.customers WHERE id = 1;\n", 1_200));

    // Lets the background read finish and its posted answer run.
    private static void SettleBackground()
    {
        Thread.Sleep(300);
        Ui.Settle();
    }

    [Test]
    public async Task A_long_script_still_completes_once_the_background_read_is_back()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open(LongScript + "SELECT * FROM public.customers c WHERE c.na|");

            Ui.Press(window, CommandId.Completion);
            SettleBackground();
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor).EndsWith("WHERE c.name|", StringComparison.Ordinal)).IsTrue();
            window.Close();
        });
    }

    [Test]
    public async Task An_answer_for_text_that_changed_meanwhile_is_dropped()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open(LongScript + "SELECT * FROM public.customers c WHERE c.na|");

            // The request goes out, then the text changes before its answer lands.
            window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.None, null);
            editor.Document.Insert(editor.CaretOffset, "m");
            SettleBackground();
            Ui.Press(window, Key.Enter);

            // Enter wrote a newline: the stale list never opened to take it.
            await Assert.That(Marked(editor).ReplaceLineEndings("\n").EndsWith("WHERE c.nam\n|", StringComparison.Ordinal)).IsTrue();
            window.Close();
        });
    }

    // --- The Enter rule (§6.1): Enter takes only a chosen or confidently typed row ---

    [Test]
    public async Task Enter_after_a_list_that_opened_by_itself_is_a_newline()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE|");

            Ui.Type(window, " "); // the space after WHERE opens the list unasked
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor).ReplaceLineEndings("\n")).IsEqualTo("SELECT * FROM public.orders o WHERE \n|");
            window.Close();
        });
    }

    [Test]
    public async Task Enter_after_only_a_loose_fuzzy_match_is_a_newline_and_tab_still_accepts()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE o.|");

            TypeKeys(window, "cid"); // matches customer_id only as a subsequence
            Ui.Press(window, Key.Enter);
            await Assert.That(Marked(editor).ReplaceLineEndings("\n")).IsEqualTo("SELECT * FROM public.orders o WHERE o.cid\n|");

            editor.Text = "SELECT * FROM public.orders o WHERE o.";
            editor.CaretOffset = editor.Text.Length;
            TypeKeys(window, "cid");
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.orders o WHERE o.customer_id|");
            window.Close();
        });
    }

    [Test]
    public async Task Enter_takes_a_row_the_user_moved_to()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE o|");

            Ui.Type(window, "."); // opens the columns of o, nothing typed yet
            Ui.Press(window, Key.Down);
            Ui.Press(window, Key.Up);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.orders o WHERE o.id|");
            window.Close();
        });
    }

    [Test]
    public async Task Enter_takes_the_top_row_of_a_list_asked_for_with_ctrl_space()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE o.|");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM public.orders o WHERE o.id|");
            window.Close();
        });
    }

    // --- The second audit's Enter rule (sql-completion-audit-2.md §6.1, A01–A07) ---
    //
    // Typed key by key over the audit stand's catalog, where each of these
    // lines used to lose its newline or be rewritten by the Enter that ends it.

    private static (Avalonia.Controls.Window Window, TextEditor Editor) OpenAuditStand(bool autoAlias)
    {
        var (window, vm, editor) = Open("|");
        vm.CompletionProvider.Load(PgNimbus.CompletionBench.AuditCatalog.Load(PgNimbus.CompletionBench.AuditCatalog.DefaultPath));
        if (vm.AutoAliasTables != autoAlias)
        {
            vm.ToggleAutoAliasCommand.Execute(null);
        }

        return (window, editor);
    }

    [Test]
    [Arguments("SELECT c.email\nFROM customers c", false)] // A02: not CROSS
    [Arguments("SELECT i.title\nFROM saas.issues i", false)] // A02: not INNER
    [Arguments("SELECT p.name\nFROM saas.projects p", true)] // A03: not saas.plans
    [Arguments("SELECT inv.number\nFROM saas.invoices inv", true)] // A03: not saas.invoices i
    [Arguments("UPDATE customers", false)] // A04: not commerce.customers
    [Arguments("DELETE FROM order_items", true)] // A04: not commerce.order_items oi
    [Arguments("SELECT *\nFROM orders", true)] // A04
    [Arguments("SELECT a.name\nFROM saas.accounts a\nWHERE a.deleted_at IS NULL", false)] // A05: not nullif(
    [Arguments("SELECT p.title\nFROM products p\nORDER BY p.title DESC", false)] // A05
    [Arguments("SELECT e.name, m.name AS manager", false)] // A06: not manager_id
    [Arguments("SELECT count(*) AS n", false)] // A06: not notifications
    [Arguments("SELECT c.email\nFROM customers c\nWHERE c.is_active = true", false)] // A07: not TRUE
    [Arguments("SELECT o.id\nFROM orders o\nJOIN customers c ON c.id = o.customer_id", false)] // A01
    [Arguments("SELECT o.id\nFROM orders o\nWHERE o.status IS NOT NULL", false)] // M: not IS NOT IS NOT NULL
    [Arguments("SELECT *", false)] // M: not "**" (the star row)
    [Arguments("SELECT o.id\nFROM orders o\nORDER BY o.id DESC NULLS LAST", false)]
    public async Task Enter_at_the_end_of_a_typed_line_is_a_newline(string line, bool autoAlias)
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias);

            foreach (var ch in line)
            {
                if (ch == '\n')
                {
                    Ui.Press(window, Key.Enter);
                }
                else
                {
                    Ui.Type(window, ch.ToString());
                }
            }

            Ui.Press(window, Key.Enter);

            await Assert.That(Lf(Marked(editor))).IsEqualTo(line + "\n|");
            window.Close();
        });
    }

    [Test]
    [Arguments("SELECT * FROM orders o WHERE o.id = 1 OB", "SELECT * FROM orders o WHERE o.id = 1 ORDER BY|")]
    [Arguments("SELECT * FROM orders o lj", "SELECT * FROM orders o left join|")] // lower case typed: lower case written
    [Arguments("SELECT * FROM orders o WHERE o.status INN", "SELECT * FROM orders o WHERE o.status IS NOT NULL|")]
    [Arguments("SELECT row_number() OVER (PB", "SELECT row_number() OVER (PARTITION BY|)")]
    public async Task Tab_on_a_phrase_row_writes_the_whole_phrase(string typed, string expected)
    {
        // C02: the initials find the phrase, and one accept writes all of it.
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, typed);
            Ui.Press(window, Key.Tab);

            await Assert.That(Marked(editor)).IsEqualTo(expected);
            window.Close();
        });
    }

    [Test]
    public async Task A_quote_after_an_enum_comparison_lists_its_labels_and_tab_writes_one()
    {
        // E07: the string's own quotes are kept; the label goes between them.
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT * FROM saas.issues i WHERE i.status = '");
            await Assert.That(PopupIsOpen(window)).IsTrue();

            TypeKeys(window, "bl");
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM saas.issues i WHERE i.status = 'blocked|'");

            editor.Text = "";
            TypeKeys(window, "SELECT * FROM public.customers c WHERE c.email = '");
            await Assert.That(PopupIsOpen(window)).IsFalse(); // an ordinary string
            window.Close();
        });
    }

    [Test]
    public async Task One_accept_writes_a_whole_construct_and_leaves_the_caret_inside_it()
    {
        // §6.4: the join with its condition; a window function with its
        // window, the caret in OVER's parentheses; a column list with the
        // caret in the first value.
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT * FROM orders o JOIN cust");
            Ui.Press(window, Key.Down);
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM orders o JOIN customers c ON c.id = o.customer_id|");

            editor.Text = "";
            TypeKeys(window, "SELECT row_num");
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT row_number() OVER (|)");

            editor.Text = "";
            TypeKeys(window, "INSERT INTO customers ");
            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).EndsWith(") VALUES (|)");
            window.Close();
        });
    }

    // --- Package R: columns first ---

    [Test]
    public async Task A_column_taken_before_any_from_writes_its_from_in_one_undo_step()
    {
        // E08: "SELECT first_n" + Tab → the column, and its table's FROM below.
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT first_n");
            Ui.Press(window, Key.Tab);

            await Assert.That(Lf(Marked(editor))).IsEqualTo("SELECT first_name|\nFROM customers");
            editor.Undo();
            await Assert.That(Lf(editor.Text)).IsEqualTo("SELECT first_n");
            window.Close();
        });
    }

    [Test]
    public async Task Enter_leaves_a_typed_column_alone_before_its_from()
    {
        // A guess: a name typed in full stays as typed, and Enter is a newline.
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT first_name");
            Ui.Press(window, Key.Enter);

            await Assert.That(Lf(Marked(editor))).IsEqualTo("SELECT first_name\n|");
            window.Close();
        });
    }

    // --- Package Q: display and settings ---

    [Test]
    public async Task The_three_completion_settings_change_what_accepting_does()
    {
        // §6.7: keyword case, the schema always, Enter or Tab only. Set through
        // the Preferences page's view model, as a person would.
        await Ui.Run(async () =>
        {
            var (window, vm, editor) = Open("|");
            vm.CompletionProvider.Load(PgNimbus.CompletionBench.AuditCatalog.Load(PgNimbus.CompletionBench.AuditCatalog.DefaultPath));
            var preferences = new PreferencesViewModel(vm);

            preferences.KeywordCaseIndex = 1; // UPPER
            TypeKeys(window, "sel");
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).IsEqualTo("SELECT|");

            editor.Text = "";
            preferences.KeywordCaseIndex = 2; // lower
            TypeKeys(window, "SEL");
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).IsEqualTo("select|");

            // Tab only: a typed prefix no longer lets Enter take the row.
            editor.Text = "";
            preferences.CompletionEnterAccepts = false;
            TypeKeys(window, "SELECT * FROM cust");
            Ui.Press(window, Key.Enter);
            await Assert.That(Lf(Marked(editor))).IsEqualTo("SELECT * FROM cust\n|");
            preferences.CompletionEnterAccepts = true;

            // The schema always: the snapshot is rebuilt off the UI thread.
            editor.Text = "";
            preferences.CompletionAlwaysQualifyTables = true;
            for (var i = 0; i < 200 && vm.CompletionProvider.GetCompletionData("SELECT * FROM cust", 18).All(d => d.InsertText != "public.customers"); i++)
            {
                await Task.Delay(10);
            }

            TypeKeys(window, "SELECT * FROM cust");
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).StartsWith("SELECT * FROM public.customers");
            preferences.CompletionAlwaysQualifyTables = false;
            window.Close();
        });
    }

    [Test]
    public async Task An_open_paren_takes_the_function_being_typed()
    {
        // F05: "coun(" writes count() with the caret inside, not coun().
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT coun(");

            await Assert.That(Marked(editor)).IsEqualTo("SELECT count(|)");
            window.Close();
        });
    }

    [Test]
    public async Task Home_and_end_move_the_caret_not_the_list()
    {
        // G05.
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT * FROM cust");
            await Assert.That(PopupIsOpen(window)).IsTrue();

            Ui.Press(window, Key.Home);
            await Assert.That(PopupIsOpen(window)).IsFalse();
            await Assert.That(editor.CaretOffset).IsEqualTo(0);
            window.Close();
        });
    }

    [Test]
    public async Task The_argument_hint_goes_when_the_editor_loses_focus()
    {
        // G06: it used to stay over the command palette.
        await Ui.Run(async () =>
        {
            var (window, vm, editor) = Open("|");
            vm.CompletionProvider.Load(PgNimbus.CompletionBench.AuditCatalog.Load(PgNimbus.CompletionBench.AuditCatalog.DefaultPath));
            TypeKeys(window, "SELECT round(");
            var hint = window.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "SignaturePopup");
            await Assert.That(hint.IsOpen).IsTrue();

            _ = vm.OpenCommandPaletteAsync();
            Ui.Settle();
            await Assert.That(hint.IsOpen).IsFalse();

            // And when focus goes anywhere else: the sidebar's filter box.
            vm.CommandPalette.CloseCommand.Execute(null);
            editor.TextArea.Focus();
            TypeKeys(window, ", ");
            Ui.Press(window, CommandId.ParameterHints);
            await Assert.That(hint.IsOpen).IsTrue();
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Focusable).Focus();
            Ui.Settle();
            await Assert.That(hint.IsOpen).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task A_row_shows_the_letters_it_matched_in_bold()
    {
        // G01: "oi" finds order_items by the starts of its parts; the row says so.
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT * FROM oi");
            Ui.Settle();
            var label = window.GetVisualDescendants().OfType<CompletionLabel>().First(l => l.Label == "order_items");
            var bold = label.Inlines!.OfType<Run>().Where(r => r.FontWeight == FontWeight.Bold).Select(r => r.Text ?? "").ToList();

            await Assert.That(bold).IsEquivalentTo(new[] { "o", "i" });
            window.Close();
        });
    }

    [Test]
    public async Task Punctuation_closes_the_list_and_a_space_does_not_filter_a_phrase()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT * FROM orders o WHERE o.status IS");
            await Assert.That(PopupIsOpen(window)).IsTrue();

            // The space ends "IS": no list keeps filtering "IS …" across it.
            TypeKeys(window, " ");
            await Assert.That(PopupIsOpen(window)).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task At_an_alias_tab_and_a_chosen_row_still_accept()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenAuditStand(autoAlias: false);
            TypeKeys(window, "SELECT * FROM customers c");
            Ui.Press(window, Key.Tab);
            await Assert.That(Marked(editor)).IsNotEqualTo("SELECT * FROM customers c|");

            editor.Text = "";
            TypeKeys(window, "SELECT * FROM customers c");
            Ui.Press(window, Key.Down);
            Ui.Press(window, Key.Up);
            Ui.Press(window, Key.Enter);
            await Assert.That(Marked(editor)).IsNotEqualTo("SELECT * FROM customers c|");
            await Assert.That(Lf(editor.Text)).DoesNotContain("\n");
            window.Close();
        });
    }

    // --- Found live (2026-09-22) ---

    // Typed "commerce." then "orde": the list showed orders on top but kept
    // order_items highlighted, and Tab wrote order_items.
    [Test]
    public async Task Typing_after_a_schema_dot_preselects_the_best_match_not_an_earlier_row()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, editor) = Open("SELECT * FROM public|");
            vm.CompletionProvider.Load(new CompletionCatalog(
                ["public"],
                [
                    new CompletionTable("public", "customers", [new TableColumn("customers", "id", "int4")]),
                    new CompletionTable("public", "order_items", [new TableColumn("order_items", "id", "int4")]),
                    new CompletionTable("public", "orders", [new TableColumn("orders", "id", "int4")]),
                ],
                [],
                [],
                ["public"]));

            TypeKeys(window, ".orde");
            Ui.Press(window, Key.Tab);

            await Assert.That(Marked(editor)).StartsWith("SELECT * FROM public.orders");
            window.Close();
        });
    }

    // The popup row the Enter rule would not take: its fill is what the eye
    // reads, so the test reads the template part that paints it, not the class.
    private static IBrush? SelectedRowFill(TextEditor editor, bool pointerOver = false)
    {
        var list = TopLevel.GetTopLevel(editor)!.GetVisualDescendants().OfType<CompletionListBox>().Single();
        var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsSelected);
        if (pointerOver)
        {
            ((IPseudoClasses)row.Classes).Add(":pointerover");
            Ui.Settle();
        }

        return row.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter").Background;
    }

    private static bool PopupIsOpen(Avalonia.Controls.Window window) =>
        window.GetVisualDescendants().OfType<CompletionListBox>().Any(l => l.IsEffectivelyVisible);

    private static bool Paints(IBrush? brush) =>
        brush is ISolidColorBrush solid && solid.Color.A > 0 && solid.Opacity > 0;

    [Test]
    public async Task A_loose_fuzzy_match_is_drawn_as_an_outline_not_a_fill()
    {
        await Ui.Run(async () =>
        {
            var (window, _, editor) = Open("SELECT * FROM public.orders o WHERE o.|");

            TypeKeys(window, "c"); // a prefix of customer_id: Enter takes it, filled
            await Assert.That(Paints(SelectedRowFill(editor))).IsTrue();

            TypeKeys(window, "id"); // "cid" is only a subsequence: Enter won't, outlined
            await Assert.That(Paints(SelectedRowFill(editor))).IsFalse();
            await Assert.That(Paints(SelectedRowFill(editor, pointerOver: true))).IsFalse();
            window.Close();
        });
    }

    // The exact text of the live report, with the caret put after "use" the
    // three ways a person does it. (The reported output, "use audit.users u",
    // is what an accept from the *end* of the line produces — the token there
    // is "u" — so the caret never reached "use" in that run.)
    private static (Avalonia.Controls.Window Window, TextEditor Editor) OpenWithUsers(string marked)
    {
        var (window, vm, editor) = Open(marked);
        vm.CompletionProvider.Load(new CompletionCatalog(
            ["public", "audit"],
            [
                new CompletionTable("public", "users", [new TableColumn("users", "id", "int4")]),
                new CompletionTable("audit", "users", [new TableColumn("users", "id", "int4")]),
                new CompletionTable("public", "big", [new TableColumn("big", "id", "int4")]),
            ],
            [],
            [],
            ["public"]));
        if (!vm.AutoAliasTables)
        {
            vm.ToggleAutoAliasCommand.Execute(null);
        }

        return (window, editor);
    }

    [Test]
    public async Task A_table_accepted_before_a_one_letter_alias_replaces_the_prefix_caret_set_by_arrows()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenWithUsers("|");
            TypeKeys(window, "SELECT * FROM use u");
            Ui.Press(window, Key.Left);
            Ui.Press(window, Key.Left);

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM users| u");
            window.Close();
        });
    }

    [Test]
    public async Task A_table_accepted_before_a_one_letter_alias_replaces_the_prefix_caret_set_by_a_click()
    {
        await Ui.Run(async () =>
        {
            var (window, editor) = OpenWithUsers("SELECT * FROM bi b|");
            var view = editor.TextArea.TextView;
            var at = view.GetVisualPosition(new TextViewPosition(1, 17), VisualYPosition.LineMiddle) - view.ScrollOffset;
            var point = view.TranslatePoint(new Point(at.X, at.Y), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Ui.Settle();
            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM bi| b");

            Ui.Press(window, CommandId.Completion);
            Ui.Press(window, Key.Enter);

            await Assert.That(Marked(editor)).IsEqualTo("SELECT * FROM big| b");
            window.Close();
        });
    }
}
