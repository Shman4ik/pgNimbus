using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.Converters;
using PgNimbus.App.ViewModels;
using PgNimbus.App.ViewModels.Security;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A status line too long for its window is cut with an ellipsis, and its whole
/// text has to be readable somewhere. The 1.0.1 release pass found a failed
/// safe-mode commit shown as "Commit failed…" with the reason out of reach: the
/// main window's status text had carried a tooltip since the bar was built, but
/// a TextBlock with no background is not hit-testable, so the pointer reached
/// the bar behind it and the tooltip never opened. The secondary windows' status
/// lines had no tooltip and no ellipsis at all.
/// </summary>
public class StatusLineTests
{
    private const string Failure =
        "Commit failed — no staged changes were applied: 23505: duplicate key value violates unique constraint "
        + "\"order_lines_pkey\". DETAIL: Key (order_id, line)=(1042, 3) already exists. The staged edits are kept, "
        + "so the offending change can be fixed, restaged or discarded before committing again.";

    [Test]
    public async Task A_cut_status_message_shows_its_whole_text_on_hover()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.ActiveTab.Status = Failure;
            vm.ActiveTab.HasError = true;
            Ui.Settle();

            var status = TextBlockShowing(window, Failure);
            await Assert.That(CutTextTip.IsCut(status)).IsTrue();
            await Assert.That(await HoverAsync(window, status)).IsEqualTo(Failure);

            window.Close();
        });
    }

    /// <summary>
    /// The live failure: Npgsql puts a server error's DETAIL on a second line,
    /// and the status bar grew a line to show it, each line cut on its own.
    /// </summary>
    [Test]
    public async Task A_message_of_two_lines_shows_one_and_its_whole_text_on_hover()
    {
        await Ui.Run(async () =>
        {
            const string twoLines = "Commit failed — no staged changes were applied: 23505: duplicate key value violates unique constraint \"orders_code_key\"\n"
                + "DETAIL: Detail redacted as it may contain sensitive data.";
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.ActiveTab.Status = "Ready";
            Ui.Settle();
            var oneLineHeight = TextBlockShowing(window, "Ready").Bounds.Height;

            vm.ActiveTab.Status = twoLines;
            Ui.Settle();

            var status = TextBlockShowing(window, twoLines);
            await Assert.That(status.Bounds.Height).IsEqualTo(oneLineHeight);
            await Assert.That(CutTextTip.IsCut(status)).IsTrue();
            await Assert.That(await HoverAsync(window, status)).IsEqualTo(twoLines);

            window.Close();
        });
    }

    [Test]
    public async Task A_status_message_that_fits_has_no_tooltip()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.ActiveTab.Status = "Committed 3 staged changes";
            Ui.Settle();

            var status = TextBlockShowing(window, "Committed 3 staged changes");
            await Assert.That(CutTextTip.IsCut(status)).IsFalse();
            await Assert.That(ToolTip.GetTip(status)).IsNull();

            window.Close();
        });
    }

    [Test]
    public async Task Every_status_bar_segment_with_a_tooltip_answers_the_pointer_over_its_text()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.IsInTransaction = true;
            vm.ActiveTab.ReadOnlyHint = "the result has no primary key";
            Ui.Settle();

            var bar = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("statusBar"));
            var segments = bar.GetVisualDescendants().OfType<StackPanel>()
                .Where(p => p.IsEffectivelyVisible && ToolTip.GetTip(p) is not null)
                .ToList();
            await Assert.That(segments.Count).IsGreaterThanOrEqualTo(2);

            foreach (var segment in segments)
            {
                var label = segment.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
                var point = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), window)!.Value;
                var hit = window.InputHitTest(point) as Visual;
                var reaches = hit is not null && (ReferenceEquals(hit, segment) || hit.GetVisualAncestors().Contains(segment));
                await Assert.That(reaches).IsTrue().Because($"the pointer over \"{label.Text}\" must reach its segment");
            }

            window.Close();
        });
    }

    [Test]
    public async Task Each_window_status_line_is_cut_with_its_whole_text_on_hover()
    {
        await Ui.Run(async () =>
        {
            foreach (var (window, setStatus) in new (Window, Action<string>)[]
            {
                Open(Scenarios.DatabaseOverview(), w => ((DatabaseOverviewViewModel)w.DataContext!).Status = Failure),
                Open(Scenarios.Security(), w => ((SecurityViewModel)w.DataContext!).Status = Failure),
                Open(Scenarios.Activity(), w => ((ActivityViewModel)w.DataContext!).Status = Failure),
            })
            {
                setStatus(Failure);
                window.Width = 700;
                Ui.Settle();

                var status = TextBlockShowing(window, Failure);
                await Assert.That(CutTextTip.IsCut(status)).IsTrue().Because(window.GetType().Name);
                await Assert.That(await HoverAsync(window, status)).IsEqualTo(Failure).Because(window.GetType().Name);
                window.Close();
            }
        });

        static (Window, Action<string>) Open(Window window, Action<Window> set)
        {
            Ui.Show(window);
            return (window, _ => set(window));
        }
    }

    private static TextBlock TextBlockShowing(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text && t.IsEffectivelyVisible);

    // Moves the pointer onto the start of the text, where the glyphs are, and
    // returns what the tooltip that opens shows. The show delay is a dispatcher
    // timer the headless clock never advances, so it is zero here; everything
    // else is the real path (hit test, pointer-over, the tooltip service).
    private static async Task<object?> HoverAsync(Window window, TextBlock block)
    {
        ToolTip.SetShowDelay(block, 0);
        var point = block.TranslatePoint(new Point(8, block.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        Ui.SettleUntil(() =>
        {
            Thread.Sleep(10);
            return ToolTip.GetIsOpen(block);
        }, maxPasses: 100);

        var tip = ToolTip.GetIsOpen(block) ? ToolTip.GetTip(block) : null;
        window.MouseMove(new Point(1, 1));
        Ui.Settle();
        await Task.CompletedTask;
        return tip;
    }
}
