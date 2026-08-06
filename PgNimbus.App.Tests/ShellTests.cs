using Avalonia;
using Avalonia.Input;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The shell window: that it stands up at all, and that the gestures and
/// commands wired through it reach the view model.
/// </summary>
public class ShellTests
{
    /// <summary>
    /// The headless session must set the application up without ever giving it a
    /// lifetime. With one, <see cref="App.OnFrameworkInitializationCompleted"/>
    /// runs, which reads the developer's real <c>AppSettings</c> and — with
    /// <c>AutoConnectLastProfile</c> on — would try to connect to their last
    /// database from a unit test. Asserted rather than assumed, because the day
    /// it changes the symptom would be a mysterious network timeout.
    /// </summary>
    [Test]
    public async Task Headless_session_runs_the_app_without_a_lifetime()
    {
        await Ui.Run(async () =>
        {
            await Assert.That(Application.Current).IsNotNull();
            await Assert.That(Application.Current!.ApplicationLifetime).IsNull();
        });
    }

    [Test]
    public async Task Shell_opens_with_one_tab_holding_the_seeded_result()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            await Assert.That(vm.Tabs).Count().IsEqualTo(1);
            await Assert.That(vm.ActiveTab.ColumnNames).IsNotEmpty();
            await Assert.That(vm.ActiveTab.Rows).Count().IsEqualTo(20);
            await Assert.That(vm.ActiveTab.RowCountText).IsEqualTo("20 rows");

            window.Close();
        });
    }

    /// <summary>
    /// End to end for UI design rule 5: the chord is declared once in the
    /// command catalog and a real key press has to travel the whole path from
    /// the window's key handling to the palette opening.
    /// </summary>
    [Test]
    public async Task Command_palette_chord_opens_the_palette()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            await Assert.That(vm.CommandPalette.IsOpen).IsFalse();

            Ui.Press(window, CommandId.CommandPalette);

            await Assert.That(vm.CommandPalette.IsOpen).IsTrue();

            window.Close();
        });
    }

    /// <summary>
    /// The palette's actual job: filter to an entry and run it. Word wrap is the
    /// target because its effect is a plain observable flag, so a failure points
    /// at the palette rather than at what the command does.
    /// </summary>
    [Test]
    public async Task Palette_invokes_the_highlighted_entry()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var wrapBefore = vm.WordWrapEditor;

            await vm.OpenCommandPaletteAsync();
            vm.CommandPalette.SearchText = "word wrap";
            Ui.Settle();

            await Assert.That(vm.CommandPalette.Results).IsNotEmpty();
            await Assert.That(vm.CommandPalette.SelectedItem!.Title).Contains("wrap", StringComparison.OrdinalIgnoreCase);

            await vm.CommandPalette.AcceptAsync();
            Ui.Settle();

            await Assert.That(vm.WordWrapEditor).IsEqualTo(!wrapBefore);
            await Assert.That(vm.CommandPalette.IsOpen).IsFalse();

            window.Close();
        });
    }

    /// <summary>
    /// The cheat sheet opens as an OverlayPanel over the shell (UI design rule 8)
    /// and Escape takes it back down. Both directions, because an overlay that
    /// cannot be dismissed traps the window.
    /// </summary>
    [Test]
    public async Task Shortcuts_overlay_opens_and_closes()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            Ui.Press(window, CommandId.ShortcutsWindow);
            await Assert.That(vm.IsShortcutsOpen).IsTrue();

            Ui.Press(window, Key.Escape);
            await Assert.That(vm.IsShortcutsOpen).IsFalse();

            window.Close();
        });
    }

    /// <summary>
    /// Every window the app can show is constructed, shown, laid out and closed.
    /// The screenshot harness renders the same set, but only on Linux in CI;
    /// this runs everywhere and reaches what a render-and-exit pass never does —
    /// the detach path, where a panel that resolved resources on attach or left
    /// an event subscribed goes wrong.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ScenarioNames))]
    public async Task Scenario_window_opens_and_closes(string name)
    {
        await Ui.Run(async () =>
        {
            var build = Scenarios.All.Single(scenario => scenario.Name == name).Build;

            var window = build();
            Ui.Show(window);
            await Assert.That(window.IsVisible).IsTrue();

            window.Close();
            Ui.Settle();
        });
    }

    public static IEnumerable<Func<string>> ScenarioNames() =>
        Scenarios.All.Select(scenario => (Func<string>)(() => scenario.Name));
}
