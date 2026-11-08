using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The plan header's Compare and Export chips open their menus on a real click. The
/// Query Lab tests open the Compare menu with <c>ShowAt</c>, which skips the pointer
/// path; the 1.3.0 release pass reported both chips showing only their tooltip.
/// </summary>
public class PlanHeaderMenuClickTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Test]
    [Arguments("Compare ▾")]
    [Arguments("Export ▾")]
    public async Task A_click_on_the_plan_header_chip_opens_its_menu(string content)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.OpenImportedPlan(ExplainService.Import(Fixture("plan-after-index.json")));
            Ui.Settle();

            var chip = window.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == content);
            await Assert.That(chip.IsEffectivelyVisible).IsTrue();
            var flyout = (MenuFlyout)chip.Flyout!;
            await Assert.That(flyout.IsOpen).IsFalse();

            var centre = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
            window.MouseMove(centre);
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Ui.Settle();

            await Assert.That(flyout.IsOpen).IsTrue();
            await Assert.That(flyout.Items.OfType<MenuItem>().Count()).IsGreaterThanOrEqualTo(3);

            flyout.Hide();
            window.Close();
        });
    }
}
