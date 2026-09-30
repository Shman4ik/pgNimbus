using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A double-click on a table browses it (UI design rule 2), and that is all it
/// does. It also used to expand the table's node, because the TreeViewItem
/// toggles itself on a double tap before the tree's handler sees it, so every
/// table you opened left its column list open in the sidebar.
/// </summary>
public class SchemaTreeDoubleClickTests
{
    [Test]
    public async Task Double_clicking_a_table_browses_it_without_expanding_its_node()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var item = window.GetVisualDescendants().OfType<TreeViewItem>()
                .First(i => i.DataContext is TableNode { Name: "customers" });
            await Assert.That(item.ItemCount).IsGreaterThan(0); // it has something to expand
            await Assert.That(item.IsExpanded).IsFalse();

            var header = item.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Header");
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 3, header.Bounds.Height / 2), window)!.Value;
            var tabsBefore = vm.Tabs.Count;

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Ui.Settle();

            // The control: the double-click did land as one, and browsed.
            await Assert.That(vm.Tabs.Count).IsEqualTo(tabsBefore + 1);
            await Assert.That(item.IsExpanded).IsFalse();

            window.Close();
        });
    }
}
