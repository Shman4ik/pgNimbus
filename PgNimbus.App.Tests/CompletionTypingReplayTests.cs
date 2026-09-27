using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using PgNimbus.App.Completion;
using PgNimbus.CompletionBench;
using PgNimbus.Core.Text;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The audit corpus typed into the real editor, key by key — the two editor
/// measurements of docs/design/sql-completion-audit-2.md (section 2.3), over
/// the audit stand's catalog (tools/CompletionBench/Audit). Every other
/// completion test puts the caret into finished text; these type the text the
/// way a person does, which is where the popup can get in the way.
///
/// Both run in every build. The literal replay is the acceptance test of the
/// audit's package K (51 divergences at the audit's revision, none since); the
/// keystroke saving has a floor each package that raises it raises too, so a
/// ranking change that costs keystrokes fails the build instead of going
/// unnoticed. Run just these with
/// <c>dotnet test --project PgNimbus.App.Tests -- --treenode-filter "/*/*/CompletionTypingReplayTests/*"</c>;
/// the saving is printed into the test report (TestResults/*.tunit-report.json).
/// </summary>
[NotInParallel]
public class CompletionTypingReplayTests
{
    /// <summary>
    /// Someone who never looks at the popup types the corpus, pressing Enter
    /// at the end of each line. The editor must end up holding exactly what
    /// was typed: Enter may not take a row instead of the newline, nothing may
    /// be rewritten.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Typing_the_corpus_without_looking_changes_nothing(bool autoAlias)
    {
        var divergences = new List<string>();
        await Ui.Run(async () =>
        {
            var (window, editor) = Open(autoAlias);
            var corpus = AuditCatalog.LoadCorpus(AuditCatalog.DefaultCorpusPath);
            for (var q = 0; q < corpus.Count; q++)
            {
                var target = corpus[q];
                Reset(editor, "");
                var typed = new StringBuilder();
                foreach (var ch in target)
                {
                    var before = DescribePopup(window);
                    TypeKey(window, ch);
                    typed.Append(ch);

                    var actual = TextBeforeCaret(editor);
                    if (actual != typed.ToString())
                    {
                        var key = ch switch { '\n' => "Enter", ' ' => "Space", _ => $"'{ch}'" };
                        divergences.Add($"Q{q + 1}: `{Tail(typed.ToString()[..^1], 40)}` + {key} gave `…{Tail(actual, 40)}` ({before})");
                        // Put back what was typed and carry on, so one slip doesn't mask the rest.
                        Reset(editor, typed.ToString() + Lf(editor.Text[Math.Min(editor.Text.Length, editor.CaretOffset)..]), typed.Length);
                    }
                }

                if (Lf(editor.Text) != target)
                {
                    divergences.Add($"Q{q + 1}: final text `{Tail(Lf(editor.Text), 60)}`");
                }
            }

            window.Close();
            await Task.CompletedTask;
        });

        Console.WriteLine($"auto-alias {(autoAlias ? "on" : "off")}: {divergences.Count} divergences");
        foreach (var divergence in divergences)
        {
            Console.WriteLine(divergence);
        }

        await Assert.That(divergences).IsEmpty();
    }

    /// <summary>
    /// The keystroke saving of a user who always picks the best row: they
    /// type the corpus and take a row with Tab (after up to four Downs)
    /// whenever it writes the next stretch of the target and saves keys, and
    /// press Escape before their own Enter when Enter would otherwise take a
    /// row. The number goes to the test output; the floor stops it from
    /// getting worse than the last package delivered (the audit's baseline was
    /// 24.4% with the auto-alias off, 25.6% with it on; package L 32.9% /
    /// 33.9%; package M, on the corpus it grew to 30 queries, 35.3% / 36.4%).
    /// A package that raises the saving raises the floor, and one that adds
    /// queries to the corpus measures the base branch on them first.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Keystroke_saving_of_a_user_who_always_picks_the_best_row(bool autoAlias)
    {
        var report = new StringBuilder();
        var total = new OracleTally();
        await Ui.Run(async () =>
        {
            var (window, editor) = Open(autoAlias);
            var corpus = AuditCatalog.LoadCorpus(AuditCatalog.DefaultCorpusPath);
            for (var q = 0; q < corpus.Count; q++)
            {
                var target = corpus[q];
                Reset(editor, "");
                var tally = TypeLikeAnOracle(window, editor, target, autoAlias);
                total.Add(tally);
                report.AppendLine($"Q{q + 1}: {target.Length} characters, {tally.Keys} keys, saved {1 - (double)tally.Keys / target.Length:P0}");
            }

            window.Close();
            await Task.CompletedTask;
        });

        var saved = 1 - (double)total.Keys / total.Characters;
        Console.WriteLine($"auto-alias {(autoAlias ? "on" : "off")}: {total.Characters} characters, {total.Keys} keys, saved {saved:P1}; "
            + $"{total.Accepts} accepts, {total.Downs} Downs, {total.Escapes} Escapes before an Enter");
        Console.WriteLine(report);
        await Assert.That(saved).IsGreaterThanOrEqualTo(0.35);
    }

    private sealed class OracleTally
    {
        public long Characters;
        public long Keys;
        public long Accepts;
        public long Downs;
        public long Escapes;

        public void Add(OracleTally other)
        {
            Characters += other.Characters;
            Keys += other.Keys;
            Accepts += other.Accepts;
            Downs += other.Downs;
            Escapes += other.Escapes;
        }
    }

    // Types `target` from the empty editor, counting the keys it took.
    private static OracleTally TypeLikeAnOracle(Window window, TextEditor editor, string target, bool autoAlias)
    {
        var tally = new OracleTally { Characters = target.Length };
        var pos = 0;
        for (var guard = 0; pos < target.Length && guard < 10_000; guard++)
        {
            if (BestRow(window, editor, target, pos, autoAlias) is { } pick)
            {
                for (var d = 0; d < pick.Downs; d++)
                {
                    Ui.Press(window, Key.Down);
                }

                Ui.Press(window, Key.Tab);
                tally.Keys += pick.Downs + 1;
                tally.Downs += pick.Downs;
                tally.Accepts++;
                if (TextBeforeCaret(editor) == target[..pick.NewPos])
                {
                    pos = pick.NewPos;
                    continue;
                }

                Reset(editor, target[..pos]); // the accept wrote something else
                continue;
            }

            var ch = target[pos];
            if (ch == '\n' && PopupList(window) is { } open && !open.Classes.Contains("tentative"))
            {
                Ui.Press(window, Key.Escape); // Enter would take the row instead of the newline
                tally.Keys++;
                tally.Escapes++;
            }

            TypeKey(window, ch);
            tally.Keys++;
            pos++;
            if (TextBeforeCaret(editor) != target[..pos])
            {
                Reset(editor, target[..pos]);
            }
        }

        return tally;
    }

    // The row (within four Downs of the selection) whose accept writes the
    // most of the target's next stretch, if taking it saves keys.
    private static (int Downs, int NewPos)? BestRow(Window window, TextEditor editor, string target, int pos, bool autoAlias)
    {
        if (PopupList(window) is not { ItemsSource: IEnumerable<SqlCompletionData> source } list
            || list.SelectedItem is not SqlCompletionData selected)
        {
            return null;
        }

        var rows = source.ToList();
        var selectedIndex = rows.IndexOf(selected);
        var text = Lf(editor.Text);
        (int Downs, int NewPos)? best = null;
        for (var r = selectedIndex; r >= 0 && r < rows.Count && r - selectedIndex <= 4; r++)
        {
            var item = rows[r];
            var edit = CompletionEdits.Plan(text, pos, item.InsertText, item.InsertKind, autoAlias ? item.AliasTable : null);
            var after = text[..edit.ReplaceStart] + edit.InsertText + text[(edit.ReplaceStart + edit.ReplaceLength)..];
            var newPos = edit.CaretOffset;
            if (newPos <= pos || newPos > target.Length || after[..newPos] != target[..newPos])
            {
                continue;
            }

            var downs = r - selectedIndex;
            if (newPos - pos > downs + 1 && (best is null || newPos - downs > best.Value.NewPos - best.Value.Downs))
            {
                best = (downs, newPos);
            }
        }

        return best;
    }

    private static (Window Window, TextEditor Editor) Open(bool autoAlias)
    {
        var (window, vm) = Scenarios.Shell();
        vm.CompletionProvider.Load(AuditCatalog.Load(AuditCatalog.DefaultPath));
        if (vm.AutoAliasTables != autoAlias)
        {
            vm.ToggleAutoAliasCommand.Execute(null);
        }

        Ui.Show(window);
        var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
        editor.TextArea.Focus();
        return (window, editor);
    }

    private static void TypeKey(Window window, char ch)
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

    private static void Reset(TextEditor editor, string text, int? caret = null)
    {
        editor.Text = text;
        editor.CaretOffset = caret ?? text.Length;
        Ui.Press((Window)TopLevel.GetTopLevel(editor)!, Key.Escape);
    }

    private static CompletionListBox? PopupList(Window window) =>
        window.GetVisualDescendants().OfType<CompletionListBox>().FirstOrDefault(l => l.IsEffectivelyVisible);

    private static string DescribePopup(Window window)
    {
        if (PopupList(window) is not { } list)
        {
            return "popup closed";
        }

        var selected = (list.SelectedItem as SqlCompletionData)?.Label ?? "-";
        var tentative = list.Classes.Contains("tentative") ? ", tentative" : "";
        return $"popup selected `{selected}`{tentative}";
    }

    // Enter writes the platform newline; the corpus uses \n.
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private static string TextBeforeCaret(TextEditor editor) =>
        Lf(editor.Text[..Math.Min(editor.CaretOffset, editor.Text.Length)]);

    private static string Tail(string text, int length) =>
        text[Math.Max(0, text.Length - length)..].Replace("\n", "⏎", StringComparison.Ordinal);
}
