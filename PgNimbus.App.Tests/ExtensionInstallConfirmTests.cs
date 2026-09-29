using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Security audit 2026-09, finding 6: dropping an extension confirmed, but
/// installing one — CREATE EXTENSION against the connected database — did
/// not. "Install" now confirms the same way as "Drop…" right beside it,
/// naming the extension and the database (<see cref="SchemaTreeViewModel.DatabaseName"/>).
/// </summary>
public class ExtensionInstallConfirmTests
{
    private static ExtensionNode SeedNotInstalledExtension(SchemaTreeViewModel tree, SchemaService schemaService)
    {
        var group = new ExtensionsGroupNode(schemaService);
        var extension = new ExtensionNode(group, new ExtensionInfo("pg_trgm", InstalledVersion: null, DefaultVersion: "1.6", Description: "text similarity"));
        group.SeedChildren([extension]);
        group.IsExpanded = true;
        tree.Schemas.Add(group);
        return extension;
    }

    /// <summary>The extension row's DockPanel carries its context menu (SchemaTreePanel.axaml), the same shape MenuTests' ContextMenuFor reads for other node kinds.</summary>
    private static ContextMenu OpenMenuFor(Window window, ExtensionNode extension)
    {
        var panel = window.GetVisualDescendants().OfType<Panel>()
            .First(p => p.ContextMenu is not null && p.DataContext == extension);
        var menu = panel.ContextMenu!;
        menu.Open(panel);
        Ui.Settle();
        return menu;
    }

    private static MenuItem Item(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [Test]
    public async Task Declining_the_confirm_never_installs()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var extension = SeedNotInstalledExtension(vm.SchemaTree, new SchemaService(Fixtures.DataSource));
            Ui.Settle();

            var installed = new List<(string Name, bool Install)>();
            vm.SchemaTree.SetExtensionInstalledRequested = (ext, install) =>
            {
                installed.Add((ext.Name, install));
                return Task.CompletedTask;
            };

            var menu = OpenMenuFor(window, extension);
            Click(Item(menu, "Install"));
            Ui.Settle();

            var confirm = window.OwnedWindows.OfType<ConfirmDialog>().Single();
            confirm.Close(false);
            Ui.Settle();

            await Assert.That(installed).IsEmpty();

            menu.Close();
            window.Close();
        });
    }

    [Test]
    public async Task Accepting_the_confirm_names_the_extension_and_database_then_installs()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var extension = SeedNotInstalledExtension(vm.SchemaTree, new SchemaService(Fixtures.DataSource));
            Ui.Settle();

            var installed = new List<(string Name, bool Install)>();
            vm.SchemaTree.SetExtensionInstalledRequested = (ext, install) =>
            {
                installed.Add((ext.Name, install));
                return Task.CompletedTask;
            };

            var menu = OpenMenuFor(window, extension);
            Click(Item(menu, "Install"));
            Ui.Settle();

            var confirm = window.OwnedWindows.OfType<ConfirmDialog>().Single();
            await Assert.That(confirm.FindControl<TextBlock>("MessageText")!.Text)
                .IsEqualTo("Install extension \"pg_trgm\" in database \"shop\"? This runs against it directly.");
            await Assert.That((string?)confirm.FindControl<Button>("ConfirmButton")!.Content).IsEqualTo("Install");

            confirm.Close(true);
            Ui.Settle();

            await Assert.That(installed).IsEquivalentTo([("pg_trgm", true)]);

            menu.Close();
            window.Close();
        });
    }
}
