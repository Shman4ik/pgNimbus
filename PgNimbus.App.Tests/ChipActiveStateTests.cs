using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace PgNimbus.App.Tests;

/// <summary>
/// A chip Button with the <c>active</c> class draws the selection wash. That is
/// the segmented-pair look the plan header's Color and Text/Tree switches and the
/// cell inspector's View/Edit use to show which segment is on.
///
/// It is a test because the class alone proves nothing. Avalonia styles have no
/// specificity: the later of two styles setting one property wins. Once the
/// <c>Button.chip.active</c> rule moved into the shared <c>Controls.axaml</c>,
/// which loads before the base <c>Button.chip</c> rule, every active chip drew
/// transparent at 0.6 opacity. It still carried the class, and a check on
/// <c>Classes.Contains("active")</c> passed. So these tests read the template part
/// that paints the fill.
/// </summary>
public class ChipActiveStateTests
{
    [Test]
    public async Task An_active_chip_draws_the_selection_wash_and_a_plain_one_does_not()
    {
        await Ui.Run(async () =>
        {
            var (window, plain, active) = OpenPair();
            var wash = SelectionWash(active);

            await Assert.That(Paints(Fill(active))).IsTrue();
            await Assert.That(ColorOf(Fill(active))).IsEqualTo(wash.Color);
            await Assert.That(active.Opacity).IsEqualTo(1d);

            // The control for the assertions above: without it, a theme that
            // painted every chip would pass them too.
            await Assert.That(Paints(Fill(plain))).IsFalse();
            await Assert.That(plain.Opacity).IsLessThan(1d);

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task A_chip_draws_the_wash_when_its_bound_class_turns_on_and_drops_it_when_it_turns_off()
    {
        await Ui.Run(async () =>
        {
            var (window, plain, _) = OpenPair();

            // What a Classes.active binding does when its value flips.
            plain.Classes.Set("active", true);
            Ui.Settle();
            await Assert.That(Paints(Fill(plain))).IsTrue();
            await Assert.That(plain.Opacity).IsEqualTo(1d);

            plain.Classes.Set("active", false);
            Ui.Settle();
            await Assert.That(Paints(Fill(plain))).IsFalse();

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    public async Task An_active_chip_keeps_the_wash_under_the_pointer()
    {
        await Ui.Run(async () =>
        {
            var (window, _, active) = OpenPair();

            // Fluent repaints PART_ContentPresenter on hover, so this state has
            // its own rule. Without it the selected segment would turn grey
            // under the pointer.
            ((IPseudoClasses)active.Classes).Add(":pointerover");
            Ui.Settle();

            await Assert.That(ColorOf(Fill(active))).IsEqualTo(SelectionWash(active).Color);

            window.Close();
            Ui.Settle();
        });
    }

    private static (Window Window, Button Plain, Button Active) OpenPair()
    {
        var plain = new Button { Content = "Rows", Classes = { "chip" } };
        var active = new Button { Content = "Time", Classes = { "chip", "active" } };
        var window = new Window
        {
            Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { active, plain } },
            Width = 300,
            Height = 100,
        };
        Ui.Show(window);
        return (window, plain, active);
    }

    private static IBrush? Fill(Button chip) =>
        chip.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter").Background;

    private static ISolidColorBrush SelectionWash(Control anchor) =>
        anchor.TryFindResource("AppSelectionBrush", anchor.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush
            : throw new InvalidOperationException("AppSelectionBrush is not a solid brush in the shared tokens.");

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static bool Paints(IBrush? brush) =>
        brush is ISolidColorBrush solid && solid.Color.A > 0 && solid.Opacity > 0;
}
