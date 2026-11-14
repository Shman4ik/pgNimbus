using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;
using static PgNimbus.App.Tests.InspectorUi;

namespace PgNimbus.App.Tests;

/// <summary>
/// An index and a trigger offer their source like a function does: one-item
/// context menu, and a double-click that opens it in a NEW tab without
/// leaving the node expanded or the active tab overwritten.
/// </summary>
public class SchemaSourceTests
{
    private const string IndexDef = "CREATE INDEX customers_email_idx ON public.customers USING btree (email)";
    private const string TriggerDef = "CREATE TRIGGER audit AFTER UPDATE ON public.customers FOR EACH ROW EXECUTE FUNCTION audit_row()";

    [Test]
    public async Task Source_text_is_the_definition_ended_with_a_semicolon()
    {
        var index = new IndexNode(new IndexInfo("customers_email_idx", false, false, IndexDef));
        var trigger = new TriggerNode(new TriggerInfo("audit", TriggerDef + ";", true));

        await Assert.That(index.SourceSql).IsEqualTo(IndexDef + ";");
        await Assert.That(trigger.SourceSql).IsEqualTo(TriggerDef + ";");
        await Assert.That(index.SourceTitle).IsEqualTo("customers_email_idx · source");
    }

    [Test]
    public async Task Index_and_trigger_menus_hold_only_source()
    {
        await Ui.Run(async () =>
        {
            var (window, _, _) = Shell();
            Ui.Show(window);

            foreach (var kind in new[] { typeof(IndexNode), typeof(TriggerNode) })
            {
                var panel = window.GetVisualDescendants().OfType<Panel>()
                    .First(p => p.ContextMenu is not null && p.DataContext?.GetType() == kind);
                var items = panel.ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Header as string).ToArray();
                await Assert.That(string.Join(" | ", items)).IsEqualTo("Source (DDL)");
            }

            window.Close();
        });
    }

    [Test]
    public async Task Double_click_opens_the_source_in_a_new_tab_and_keeps_the_node_collapsed_state()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, table) = Shell();
            Ui.Show(window);

            var tabsBefore = vm.Tabs.Count;
            var activeBefore = vm.ActiveTab;
            var activeSql = activeBefore.Sql;

            var row = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "audit");
            DoubleClick(window, row);

            await Assert.That(vm.Tabs.Count).IsEqualTo(tabsBefore + 1);
            await Assert.That(vm.ActiveTab).IsNotEqualTo(activeBefore);
            await Assert.That(vm.ActiveTab.Sql).IsEqualTo(TriggerDef + ";");
            await Assert.That(activeBefore.Sql).IsEqualTo(activeSql);
            await Assert.That(table.IsExpanded).IsTrue();

            window.Close();
        });
    }

    private static (Window Window, MainViewModel Vm, TableNode Table) Shell()
    {
        var (window, vm) = Scenarios.Shell();
        var table = vm.SchemaTree.Schemas.OfType<SchemaNode>().First(s => s.Name == "public")
            .Children.OfType<TableNode>().First(t => t.Name == "customers");
        table.SeedChildren(
        [
            new IndexNode(new IndexInfo("customers_email_idx", false, false, IndexDef)),
            new TriggerNode(new TriggerInfo("audit", TriggerDef, true)),
        ]);
        table.IsExpanded = true;
        return (window, vm, table);
    }
}
