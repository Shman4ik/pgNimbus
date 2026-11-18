using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The schema menu's two drops (UI rule 1): the plain one is Postgres's RESTRICT
/// and its confirm says it fails on a non-empty schema; only the Cascade item
/// confirms that everything in the schema goes with it. A release walk read the
/// Cascade text under the plain item, so each item is clicked here and its
/// dialog and the drop it asks for are read.
/// </summary>
public class DropSchemaConfirmTests
{
    [Test]
    [Arguments("Drop Schema…", false, "Drop schema \"public\"? This fails if the schema still contains objects.")]
    [Arguments("Drop Schema (Cascade)…", true, "Drop schema \"public\" and everything in it? Tables, views and their data are removed.")]
    public async Task Each_drop_item_confirms_its_own_text_and_asks_for_its_own_drop(string header, bool cascade, string message)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var drops = new List<(string Schema, bool Cascade)>();
            vm.SchemaTree.DropSchemaRequested = (schema, isCascade) =>
            {
                drops.Add((schema.Name, isCascade));
                return Task.CompletedTask;
            };

            var panel = window.GetVisualDescendants().OfType<Panel>()
                .First(p => p.ContextMenu is not null && p.DataContext is SchemaNode { Name: "public" });
            var menu = panel.ContextMenu!;
            menu.Open(panel);
            Ui.Settle();

            menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header)
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Ui.Settle();

            var confirm = window.OwnedWindows.OfType<ConfirmDialog>().Single();
            await Assert.That(confirm.FindControl<TextBlock>("MessageText")!.Text).IsEqualTo(message);

            confirm.Close(true);
            Ui.Settle();

            await Assert.That(drops).IsEquivalentTo([("public", cascade)]);

            menu.Close();
            window.Close();
        });
    }
}
