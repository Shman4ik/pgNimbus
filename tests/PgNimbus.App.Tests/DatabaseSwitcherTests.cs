using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Commands;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The title bar's database name opens a list of the server's databases, and
/// choosing one opens it in this window (Switch Connection's semantics, with a
/// profile that differs only in the database). The list and the switch are
/// seams here (<see cref="MainViewModel.ListDatabases"/>,
/// <see cref="MainViewModel.SwitchToDatabase"/>): the fixture's data source
/// never answers. The query itself is <c>SchemaServiceDatabasesTests</c>' in Core.
/// </summary>
public class DatabaseSwitcherTests
{
    private static readonly IReadOnlyList<string> Databases = ["analytics", "postgres", "shop"];

    private static string Title => CommandCatalog.Get(CommandId.SwitchDatabase).Title;

    [Test]
    public async Task The_command_is_palette_only_with_no_chord()
    {
        var descriptor = CommandCatalog.Get(CommandId.SwitchDatabase);
        await Assert.That(descriptor.Chord).IsNull();
        await Assert.That(descriptor.In(CommandSurface.Palette)).IsTrue();
        await Assert.That(descriptor.In(CommandSurface.CheatSheet)).IsFalse();
        await Assert.That(descriptor.In(CommandSurface.WindowBinding)).IsFalse();
    }

    [Test]
    public async Task The_list_marks_the_current_database_and_filters_as_typed()
    {
        var switcher = new DatabaseSwitcherViewModel("shop", _ => Task.FromResult(Databases), _ => Task.CompletedTask, null);
        await switcher.LoadAsync();

        await Assert.That(switcher.Shown.Select(d => d.Name).ToList()).IsEquivalentTo(Databases);
        await Assert.That(switcher.Shown.Single(d => d.IsCurrent).Name).IsEqualTo("shop");
        await Assert.That(switcher.Selected?.Name).IsEqualTo("shop");
        await Assert.That(switcher.HasMessage).IsFalse();
        await Assert.That(switcher.CanOpenInNewWindow).IsFalse();

        switcher.Filter = "POST";
        await Assert.That(switcher.Shown.Select(d => d.Name).ToList()).IsEquivalentTo(new[] { "postgres" });
        await Assert.That(switcher.Selected?.Name).IsEqualTo("postgres");

        switcher.Filter = "nothing";
        await Assert.That(switcher.Shown.Count).IsEqualTo(0);
        await Assert.That(switcher.Message).IsEqualTo("No database matches.");
    }

    [Test]
    public async Task A_list_that_cannot_be_read_says_why()
    {
        var switcher = new DatabaseSwitcherViewModel(
            "shop", _ => throw new InvalidOperationException("permission denied"), _ => Task.CompletedTask, null);
        await switcher.LoadAsync();

        await Assert.That(switcher.Shown.Count).IsEqualTo(0);
        await Assert.That(switcher.Message).Contains("permission denied");
    }

    [Test]
    public async Task Choosing_a_database_in_the_flyout_switches_this_window_to_it()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            var switched = new List<string>();
            vm.ListDatabases = _ => Task.FromResult(Databases);
            vm.SwitchToDatabase = name =>
            {
                switched.Add(name);
                return Task.CompletedTask;
            };
            Ui.Show(window);

            var (button, flyout, view) = OpenFlyout(window);
            await Assert.That(await Ui.WaitUntilAsync(() => Model(view).Shown.Count == 3)).IsTrue();

            // The current database is checked and starts selected.
            var shop = Row(view, "shop");
            await Assert.That(shop.GetVisualDescendants().OfType<PathIcon>().Single().IsVisible).IsTrue();
            await Assert.That(Row(view, "analytics").GetVisualDescendants().OfType<PathIcon>().Single().IsVisible).IsFalse();
            await Assert.That(Model(view).Selected?.Name).IsEqualTo("shop");

            // The filter box has the keys: type to narrow, Enter opens.
            var popup = TopLevel.GetTopLevel(view)!;
            await Assert.That(view.FindControl<TextBox>("FilterBox")!.IsFocused).IsTrue();
            Ui.Type(popup, "ana");
            await Assert.That(Model(view).Shown.Select(d => d.Name).ToList()).IsEquivalentTo(new[] { "analytics" });
            Ui.Press(popup, Key.Enter);
            Ui.Settle();

            await Assert.That(switched).IsEquivalentTo(new[] { "analytics" });
            await Assert.That(flyout.IsOpen).IsFalse();

            // A click on a row opens it too; the current one opens nothing.
            (_, flyout, view) = OpenFlyout(window);
            await Assert.That(await Ui.WaitUntilAsync(() => Model(view).Shown.Count == 3)).IsTrue();
            Row(view, "shop").RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, null!));
            Ui.Settle();
            await Assert.That(switched.Count).IsEqualTo(1);
            await Assert.That(flyout.IsOpen).IsFalse();

            (_, flyout, view) = OpenFlyout(window);
            await Assert.That(await Ui.WaitUntilAsync(() => Model(view).Shown.Count == 3)).IsTrue();
            Row(view, "postgres").RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, null!));
            Ui.Settle();
            await Assert.That(switched).IsEquivalentTo(new[] { "analytics", "postgres" });

            window.Close();
        });
    }

    [Test]
    public async Task A_failed_connect_leaves_the_window_on_its_database_and_says_why()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            vm.ListDatabases = _ => Task.FromResult(Databases);
            vm.SwitchToDatabase = _ => Task.FromException(new InvalidOperationException("permission denied for database analytics"));
            Ui.Show(window);

            await vm.SwitchToDatabaseAsync("analytics");
            Ui.Settle();

            await Assert.That(window.IsVisible).IsTrue();
            await Assert.That(vm.ConnectionDatabase).IsEqualTo("shop");
            await Assert.That(vm.ActiveTab.Status).Contains("Couldn't open database analytics");
            await Assert.That(vm.ActiveTab.Status).Contains("permission denied");

            window.Close();
        });
    }

    [Test]
    public async Task A_row_menu_opens_the_database_in_a_new_window()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            var opened = new List<string>();
            vm.ListDatabases = _ => Task.FromResult(Databases);
            vm.SwitchToDatabase = _ => Task.CompletedTask;
            vm.OpenDatabaseInNewWindow = name =>
            {
                opened.Add(name);
                return Task.CompletedTask;
            };
            Ui.Show(window);

            var (_, flyout, view) = OpenFlyout(window);
            await Assert.That(await Ui.WaitUntilAsync(() => Model(view).Shown.Count == 3)).IsTrue();
            var label = Row(view, "postgres").GetVisualDescendants().OfType<TextBlock>().First();
            label.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = label });
            Ui.Settle();

            await Assert.That(Model(view).Selected?.Name).IsEqualTo("postgres");
            var item = view.RowMenu!.Items.OfType<MenuItem>().Single();
            await Assert.That(item.Header).IsEqualTo("Open in New Window");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Ui.Settle();

            await Assert.That(opened).IsEquivalentTo(new[] { "postgres" });
            await Assert.That(flyout.IsOpen).IsFalse();
            view.RowMenu.Close();

            window.Close();
        });
    }

    [Test]
    public async Task The_palette_lists_the_command_only_where_the_window_can_switch_and_it_opens_the_list()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            vm.ListDatabases = _ => Task.FromResult(Databases);
            Ui.Show(window);

            // Not awaited: the palette's table fetch never returns against the
            // fixture data source (see ShellTests).
            _ = vm.OpenCommandPaletteAsync();
            Ui.Settle();
            await Assert.That(vm.CommandPalette.Results.Any(item => item.Title == Title)).IsFalse();
            vm.CommandPalette.CloseCommand.Execute(null);
            Ui.Settle();

            vm.SwitchToDatabase = _ => Task.CompletedTask;
            Ui.Settle();
            _ = vm.OpenCommandPaletteAsync();
            vm.CommandPalette.SearchText = "Switch database";
            Ui.Settle();
            await Assert.That(vm.CommandPalette.SelectedItem?.Title).IsEqualTo(Title);
            await vm.CommandPalette.AcceptAsync();
            Ui.Settle();

            var button = window.FindControl<Button>("DatabaseSwitchButton")!;
            var flyout = (Flyout)button.Flyout!;
            await Assert.That(flyout.IsOpen).IsTrue();
            var view = (DatabaseSwitcherView)flyout.Content!;
            await Assert.That(await Ui.WaitUntilAsync(() => Model(view).Shown.Count == 3)).IsTrue();
            flyout.Hide();

            window.Close();
        });
    }

    /// <summary>
    /// At rest the button must not move the name: its padding is taken back by a
    /// negative margin, so the screenshot baselines (whose fixture can't switch,
    /// and so shows the plain text) still match a real window's bar.
    /// </summary>
    [Test]
    public async Task At_rest_the_database_name_sits_where_the_plain_text_did()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var button = window.FindControl<Button>("DatabaseSwitchButton")!;
            var breadcrumb = window.FindControl<StackPanel>("ConnectionBreadcrumb")!;
            await Assert.That(button.IsVisible).IsFalse();

            var plain = breadcrumb.Children.OfType<TextBlock>().Last(t => t.IsVisible);
            var before = plain.TranslatePoint(default, window)!.Value;
            var widthBefore = breadcrumb.Bounds.Width;

            vm.SwitchToDatabase = _ => Task.CompletedTask;
            Ui.Settle();
            await Assert.That(button.IsVisible).IsTrue();
            await Assert.That(plain.IsVisible).IsFalse();

            var inButton = button.GetVisualDescendants().OfType<TextBlock>().Single();
            var after = inButton.TranslatePoint(default, window)!.Value;
            await Assert.That(after).IsEqualTo(before);
            await Assert.That(inButton.Bounds.Size).IsEqualTo(plain.Bounds.Size);
            await Assert.That(breadcrumb.Bounds.Width).IsEqualTo(widthBefore);

            // Drawn the same: the face, weight and colour the plain text had.
            await Assert.That(inButton.FontFamily).IsEqualTo(plain.FontFamily);
            await Assert.That(inButton.FontWeight).IsEqualTo(plain.FontWeight);
            await Assert.That((inButton.Foreground as Avalonia.Media.ISolidColorBrush)?.Color)
                .IsEqualTo((plain.Foreground as Avalonia.Media.ISolidColorBrush)?.Color);
            await Assert.That((button.Background as Avalonia.Media.ISolidColorBrush)?.Color.A ?? 0).IsEqualTo((byte)0);

            window.Close();
        });
    }

    private static (Button Button, Flyout Flyout, DatabaseSwitcherView View) OpenFlyout(Window window)
    {
        var button = window.FindControl<Button>("DatabaseSwitchButton")!;
        var flyout = (Flyout)button.Flyout!;
        flyout.ShowAt(button);
        Ui.Settle();
        return (button, flyout, (DatabaseSwitcherView)flyout.Content!);
    }

    private static DatabaseSwitcherViewModel Model(DatabaseSwitcherView view) => (DatabaseSwitcherViewModel)view.DataContext!;

    private static ListBoxItem Row(DatabaseSwitcherView view, string name)
    {
        Ui.Settle();
        return view.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(i => i.DataContext is DatabaseSwitcherItem item && item.Name == name);
    }
}
