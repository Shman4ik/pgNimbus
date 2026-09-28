using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.Views;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The modal dialogs' shared shape (DESIGN.md rule 16), and the connection
/// dialog's layout. All of it came out of one macOS audit, and all of it applies
/// on every platform.
/// </summary>
public class DialogTests
{
    /// <summary>The screenshot scenarios that are modal dialogs.</summary>
    private static readonly string[] ModalDialogScenarios =
    [
        "security-role-dialog",
        "security-drop-role-dialog",
        "save-query-dialog",
        "staged-conflict-dialog",
        "confirm-dialog",
        "pending-changes-dialog",
        "import-plan-dialog",
        "import-dialog",
        "bulk-grant-dialog",
    ];

    public static IEnumerable<Func<string>> ModalDialogNames() =>
        ModalDialogScenarios.Select(name => (Func<string>)(() => name));

    /// <summary>
    /// A minimized modal leaves its owner blocked with nothing on screen to
    /// answer, and a maximized confirm box is one sentence in the middle of a
    /// monitor. On macOS the caption text is hidden too (the body heading is the
    /// title), but <see cref="Window.Title"/> stays set for VoiceOver and Mission
    /// Control.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ModalDialogNames))]
    public async Task A_modal_dialog_cannot_be_minimized_or_maximized(string name)
    {
        await Ui.Run(async () =>
        {
            var window = Build(name);
            Ui.Show(window);

            await Assert.That(window.CanMinimize).IsFalse();
            await Assert.That(window.CanMaximize).IsFalse();
            await Assert.That(window.ExtendClientAreaToDecorationsHint).IsEqualTo(OperatingSystem.IsMacOS());
            await Assert.That(string.IsNullOrWhiteSpace(window.Title)).IsFalse();

            window.Close();
            Ui.Settle();
        });
    }

    /// <summary>
    /// <c>[secondary…] [Cancel/Close] [Primary]</c>: the affirmative is the
    /// rightmost button and Cancel sits immediately left of it. It was the other
    /// way round ("Save | Cancel"), which on a Mac put the affirmative where every
    /// native dialog keeps Cancel.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ButtonRowNames))]
    public async Task A_dialog_ends_its_button_row_on_the_primary_with_Cancel_just_before_it(string name)
    {
        await Ui.Run(async () =>
        {
            var window = Build(name);
            Ui.Show(window);

            var row = window.GetVisualDescendants().OfType<StackPanel>()
                .Single(panel => panel.Classes.Contains("dialogButtons"));
            var buttons = row.Children.OfType<Button>().Where(button => button.IsVisible).ToList();

            var primary = buttons[^1];
            await Assert.That(IsPrimary(primary)).IsTrue();
            await Assert.That(buttons[^2].Content as string).IsIn("Cancel", "Close");
            // Nothing affirmative hides further left.
            await Assert.That(buttons.Take(buttons.Count - 1).Any(IsPrimary)).IsFalse();

            window.Close();
            Ui.Settle();
        });
    }

    public static IEnumerable<Func<string>> ButtonRowNames() =>
        ModalDialogScenarios.Where(name => name != "staged-conflict-dialog")
            .Append("crash-window")
            .Select(name => (Func<string>)(() => name));

    /// <summary>The conflict dialog lays its row out by hand (a hint shares it), so it is checked by name.</summary>
    [Test]
    public async Task The_conflict_dialog_puts_restage_last_and_close_just_before_it()
    {
        await Ui.Run(async () =>
        {
            var window = Build("staged-conflict-dialog");
            Ui.Show(window);

            var restage = window.FindControl<Button>("RestageButton")!;
            var row = (StackPanel)restage.Parent!;
            var buttons = row.Children.OfType<Button>().ToList();
            await Assert.That(buttons[^1]).IsSameReferenceAs(restage);
            await Assert.That(buttons[^2].Content as string).IsEqualTo("Close");

            window.Close();
            Ui.Settle();
        });
    }

    /// <summary>
    /// Maximized, the form used to stretch every field across the screen, with
    /// the port box ~1500px from the host it belongs to. The list and the form now
    /// stop at 1000px together and are centred, with the bar's heading over the
    /// list; in a narrow window the form column still takes all the width.
    /// </summary>
    [Test]
    public async Task The_connection_form_stops_widening_in_a_wide_window()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.ConnectionDialog();
            window.Width = 1600;
            Ui.Show(window);

            // The list and the form are one block, capped and centred.
            var layout = window.FindControl<Grid>("FormLayout")!;
            await Assert.That(layout.Bounds.Width).IsEqualTo(1000);
            await Assert.That(layout.Bounds.X).IsEqualTo((1600 - 1000) / 2.0);
            await Assert.That(layout.ColumnDefinitions[1].ActualWidth).IsEqualTo(1000 - 240);

            // Connect stays under the form's right edge, not in the window's corner.
            var connect = window.FindControl<Button>("ConnectButton")!;
            var connectRight = connect.TranslatePoint(new Point(connect.Bounds.Width, 0), window)!.Value.X;
            await Assert.That(connectRight).IsLessThanOrEqualTo(layout.Bounds.Right);

            // The bar's heading moved with the block and sits over the list.
            var heading = window.FindControl<TextBlock>("BarHeading")!;
            var headingLeft = heading.TranslatePoint(new Point(0, 0), window)!.Value.X;
            await Assert.That(Math.Abs(headingLeft - layout.Bounds.X)).IsLessThanOrEqualTo(1);

            window.Width = 640;
            Ui.Settle();
            await Assert.That(layout.ColumnDefinitions[1].ActualWidth).IsEqualTo(640 - 32 - 240);

            window.Close();
            Ui.Settle();
        });
    }

    /// <summary>
    /// New is a compact + under the list rather than a 240px bar, Connect is the
    /// rightmost button, and each switch sits right of its label, as on the
    /// Settings page.
    /// </summary>
    [Test]
    public async Task The_connection_dialog_follows_the_platform_layout()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.ConnectionDialog();
            Ui.Show(window);

            var add = window.FindControl<Button>("NewConnectionButton")!;
            await Assert.That(add.Bounds.Width).IsLessThanOrEqualTo(40);
            await Assert.That(ToolTip.GetTip(add) as string).IsEqualTo("New Connection");

            var buttons = window.FindControl<StackPanel>("FormButtons")!.Children.OfType<Button>().ToList();
            await Assert.That(buttons[^1]).IsSameReferenceAs(window.FindControl<Button>("ConnectButton"));

            foreach (var switchName in new[] { "ReadOnlySwitch", "SshTunnelSwitch" })
            {
                var toggle = window.FindControl<ToggleSwitch>(switchName)!;
                var label = ((Grid)toggle.Parent!).Children.OfType<TextBlock>().Single();
                var toggleX = toggle.TranslatePoint(default, window)!.Value.X;
                var labelX = label.TranslatePoint(default, window)!.Value.X;
                await Assert.That(toggleX).IsGreaterThan(labelX + label.Bounds.Width);
            }

            window.Close();
            Ui.Settle();
        });
    }

    /// <summary>The About overlay carries the app's mark above its name, as a native About panel does.</summary>
    [Test]
    public async Task The_about_box_shows_the_app_mark()
    {
        await Ui.Run(async () =>
        {
            var window = Build("about-window");
            Ui.Show(window);

            var about = window.GetVisualDescendants().OfType<AboutView>().Single();
            var logo = about.FindControl<Image>("LogoImage")!;
            await Assert.That(logo.Source).IsNotNull();
            await Assert.That(logo.IsEffectivelyVisible).IsTrue();

            window.Close();
            Ui.Settle();
        });
    }

    // `soft danger` is a secondary that destroys (Discard all); `danger` alone is
    // the affirmative of a destructive confirm.
    private static bool IsPrimary(Button button) =>
        button.Classes.Contains("accent") || (button.Classes.Contains("danger") && !button.Classes.Contains("soft"));

    private static Window Build(string name) =>
        Scenarios.All.Single(scenario => scenario.Name == name).Build();
}
