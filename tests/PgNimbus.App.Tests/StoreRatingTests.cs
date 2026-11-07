using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.Platform;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// "Rate pgNimbus in the Microsoft Store": a palette row, a ☰ menu item and a
/// link in the About box, all three shown only when the process has a package
/// identity, which only the Store MSIX has. Nothing about it may ever ask:
/// there is no prompt, no counter and no telemetry (UI design rule 1).
/// </summary>
public class StoreRatingTests
{
    private static string Title => CommandCatalog.Get(CommandId.RateInStore).Title;

    [Test]
    public async Task The_review_link_names_pgNimbus_in_the_Store()
    {
        await Assert.That(StoreListing.ReviewUri).IsEqualTo("ms-windows-store://review/?ProductId=9N6SZT42XJ24");
        await Assert.That(CommandCatalog.Get(CommandId.RateInStore).Chord).IsNull();
        await Assert.That(CommandCatalog.Get(CommandId.RateInStore).In(CommandSurface.CheatSheet)).IsFalse();
    }

    /// <summary>
    /// The test host is a plain process, never an MSIX, so on Windows this is the
    /// real <c>GetCurrentPackageFullName</c> answering "no package", and on every
    /// platform it is what an unpackaged install sees.
    /// </summary>
    [Test]
    public async Task All_three_entries_are_hidden_without_a_package_identity()
    {
        await Ui.Run(async () =>
        {
            await Assert.That(StoreListing.IsPackaged).IsFalse();

            var (window, vm) = Scenarios.Shell();
            var opened = 0;
            vm.OpenStoreReviewPage = () => opened++;
            await Assert.That(vm.CanRateInStore).IsFalse();
            Ui.Show(window);

            // Not awaited: the palette's table fetch never returns against the
            // fixture data source (see ShellTests).
            _ = vm.OpenCommandPaletteAsync();
            Ui.Settle();
            await Assert.That(vm.CommandPalette.Results.Any(item => item.Title == Title)).IsFalse();
            vm.CommandPalette.CloseCommand.Execute(null);

            var menuItem = OpenAppMenuItem(window);
            await Assert.That(menuItem.IsVisible).IsFalse();
            window.FindControl<Button>("AppMenuButton")!.Flyout!.Hide();

            vm.IsAboutOpen = true;
            Ui.Settle();
            var link = AboutLink(window);
            await Assert.That(link.IsEffectivelyVisible).IsFalse();

            // The command does nothing either, however it is reached.
            vm.RateInStoreCommand.Execute(null);
            await Assert.That(opened).IsEqualTo(0);

            window.Close();
        });
    }

    [Test]
    public async Task A_Store_install_shows_all_three_and_each_opens_the_review_page()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            var opened = 0;
            vm.OpenStoreReviewPage = () => opened++;
            vm.CanRateInStore = true;
            Ui.Show(window);

            _ = vm.OpenCommandPaletteAsync();
            vm.CommandPalette.SearchText = "Microsoft Store";
            Ui.Settle();
            await Assert.That(vm.CommandPalette.SelectedItem?.Title).IsEqualTo(Title);
            await vm.CommandPalette.AcceptAsync();
            Ui.Settle();
            await Assert.That(opened).IsEqualTo(1);

            var menuItem = OpenAppMenuItem(window);
            await Assert.That(menuItem.IsVisible).IsTrue();
            await Assert.That(menuItem.Header).IsEqualTo("Rate pgNimbus in the Microsoft Store");
            menuItem.Command!.Execute(null);
            await Assert.That(opened).IsEqualTo(2);
            window.FindControl<Button>("AppMenuButton")!.Flyout!.Hide();

            vm.IsAboutOpen = true;
            Ui.Settle();
            var about = window.GetVisualDescendants().OfType<AboutView>().Single();
            var aboutOpened = 0;
            about.OpenStoreReviewPage = () => aboutOpened++;
            about.ShowsStoreLink = true;
            Ui.Settle();
            var link = AboutLink(window);
            await Assert.That(link.IsEffectivelyVisible).IsTrue();
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Assert.That(aboutOpened).IsEqualTo(1);

            window.Close();
        });
    }

    // The flyout's items bind once it has opened, as they would under a click.
    private static MenuItem OpenAppMenuItem(Window window)
    {
        var menuButton = window.FindControl<Button>("AppMenuButton")!;
        menuButton.Flyout!.ShowAt(menuButton);
        Ui.Settle();
        return window.FindControl<MenuItem>("MenuRateInStore")!;
    }

    private static Button AboutLink(Window window) =>
        window.GetVisualDescendants().OfType<AboutView>().Single()
            .GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RateInStoreLink");
}
