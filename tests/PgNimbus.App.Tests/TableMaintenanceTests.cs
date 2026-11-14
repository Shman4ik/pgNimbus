using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A relation's Maintenance submenu (Analyze, Vacuum, Truncate…, Rename…, Drop…)
/// and its palette rows. Nothing reaches a server: <see cref="MainViewModel.MaintenanceRunner"/>
/// records the statements that would have been sent. The submenu's labels per
/// relation kind are pinned in <see cref="MenuTests"/>.
/// </summary>
public class TableMaintenanceTests
{
    private static (Window Window, MainViewModel ViewModel, List<string> Sent) Shell(
        bool readOnlyProfile = false, ConnectionEnvironment environment = ConnectionEnvironment.None)
    {
        var vm = Fixtures.MainWindowViewModel(readOnlyProfile: readOnlyProfile, environment: environment);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        Ui.Show(window);
        var sent = new List<string>();
        vm.MaintenanceRunner = (sql, _) =>
        {
            sent.Add(sql);
            return Task.CompletedTask;
        };
        return (window, vm, sent);
    }

    private static MainViewModel.MaintenanceTarget Customers => new("public", "customers", RelationKind.Table);

    /// <summary>Clicks a Maintenance submenu item of a tree row's context menu, as a pointer would.</summary>
    private static void ClickMaintenanceItem(Window window, string table, string header)
    {
        var panel = window.GetVisualDescendants().OfType<Panel>()
            .First(p => p.ContextMenu is not null && p.DataContext is TableNode node && node.Name == table);
        var menu = panel.ContextMenu!;
        menu.Open(panel);
        Ui.Settle();
        var maintenance = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Maintenance");
        var item = maintenance.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header);
        menu.Close();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Ui.Settle();
    }

    [Test]
    public async Task Truncate_from_the_menu_confirms_naming_the_table_and_database_and_cancel_sends_nothing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell();

            ClickMaintenanceItem(window, "customers", "Truncate…");
            var confirm = window.OwnedWindows.OfType<ConfirmDialog>().Single();
            var message = confirm.FindControl<TextBlock>("MessageText")!.Text!;
            await Assert.That(message).Contains("\"public.customers\"");
            await Assert.That(message).Contains("database \"shop\"");
            await Assert.That(message).Contains("can't be undone");
            var button = confirm.FindControl<Button>("ConfirmButton")!;
            await Assert.That((string?)button.Content).IsEqualTo("Truncate");
            await Assert.That(button.Classes.Contains("danger")).IsTrue();

            confirm.Close(false);
            Ui.Settle();
            await Assert.That(sent).IsEmpty();

            ClickMaintenanceItem(window, "customers", "Truncate…");
            window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(true);
            Ui.SettleUntil(() => sent.Count > 0);
            await Assert.That(sent).IsEquivalentTo(["TRUNCATE TABLE \"public\".\"customers\""]);
            await Assert.That(vm.ActiveTab.Status).IsEqualTo("Truncated public.customers");

            window.Close();
        });
    }

    [Test]
    public async Task Drop_confirms_with_the_kind_and_what_is_lost_and_cancel_sends_nothing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell();

            ClickMaintenanceItem(window, "daily_revenue", "Drop…");
            var confirm = window.OwnedWindows.OfType<ConfirmDialog>().Single();
            var message = confirm.FindControl<TextBlock>("MessageText")!.Text!;
            await Assert.That(message).StartsWith("Drop materialized view \"public.daily_revenue\" in database \"shop\"?");
            await Assert.That(message).Contains("its data are removed");
            await Assert.That((string?)confirm.FindControl<Button>("ConfirmButton")!.Content).IsEqualTo("Drop");

            confirm.Close(false);
            Ui.Settle();
            await Assert.That(sent).IsEmpty();

            // Not awaited: after the drop the schema refresh waits on the
            // fixture's unroutable data source.
            _ = vm.RunTableMaintenanceAsync(Customers, TableMaintenanceAction.Drop);
            Ui.Settle();
            window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(true);
            Ui.SettleUntil(() => sent.Count > 0);
            await Assert.That(sent).IsEquivalentTo(["DROP TABLE \"public\".\"customers\""]);

            window.Close();
        });
    }

    [Test]
    public async Task Rename_asks_for_the_name_in_the_naming_dialog()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell();

            ClickMaintenanceItem(window, "active_customers", "Rename…");
            var dialog = window.OwnedWindows.OfType<SaveQueryDialog>().Single();
            await Assert.That(dialog.FindControl<TextBlock>("HeadingText")!.Text).IsEqualTo("Rename view \"public.active_customers\"");
            await Assert.That(dialog.FindControl<TextBox>("NameBox")!.Text).IsEqualTo("active_customers");
            await Assert.That((string?)dialog.FindControl<Button>("SaveButton")!.Content).IsEqualTo("Rename");

            // Cancel sends nothing; so does the unchanged name.
            dialog.Close(null);
            Ui.Settle();
            ClickMaintenanceItem(window, "active_customers", "Rename…");
            window.OwnedWindows.OfType<SaveQueryDialog>().Single().Close(new SaveQueryResult("active_customers", null));
            Ui.Settle();
            await Assert.That(sent).IsEmpty();

            ClickMaintenanceItem(window, "active_customers", "Rename…");
            window.OwnedWindows.OfType<SaveQueryDialog>().Single().Close(new SaveQueryResult("Active \"Clients\"", null));
            Ui.SettleUntil(() => sent.Count > 0);
            await Assert.That(sent).IsEquivalentTo(["ALTER VIEW \"public\".\"active_customers\" RENAME TO \"Active \"\"Clients\"\"\""]);
            await Assert.That(vm.ActiveTab.Status).IsEqualTo("Renamed public.active_customers to public.Active \"Clients\"");

            window.Close();
        });
    }

    [Test]
    public async Task Analyze_and_vacuum_run_at_once_with_a_status_line()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell();

            ClickMaintenanceItem(window, "customers", "Vacuum");
            Ui.SettleUntil(() => vm.ActiveTab.Status?.StartsWith("Vacuumed", StringComparison.Ordinal) == true);
            await vm.RunTableMaintenanceAsync(Customers, TableMaintenanceAction.Analyze);

            await Assert.That(window.OwnedWindows.OfType<ConfirmDialog>()).IsEmpty();
            await Assert.That(sent).IsEquivalentTo(["VACUUM \"public\".\"customers\"", "ANALYZE \"public\".\"customers\""]);
            await Assert.That(vm.ActiveTab.Status).StartsWith("Analyzed public.customers in ");

            window.Close();
        });
    }

    [Test]
    public async Task A_failure_lands_in_the_sidebar_strip_and_the_status_line()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, _) = Shell();
            vm.MaintenanceRunner = (_, _) => throw new InvalidOperationException("permission denied for table customers");

            await vm.RunTableMaintenanceAsync(Customers, TableMaintenanceAction.Vacuum);

            await Assert.That(vm.SchemaTree.ErrorMessage).IsEqualTo("permission denied for table customers");
            await Assert.That(vm.ActiveTab.HasError).IsTrue();
            await Assert.That(vm.ActiveTab.Status).IsEqualTo("Couldn't vacuum public.customers: permission denied for table customers");

            window.Close();
        });
    }

    [Test]
    public async Task A_read_only_connection_refuses_the_writes_up_front_but_still_vacuums()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell(readOnlyProfile: true);
            await Assert.That(vm.ConnectionReadOnlyHint).IsNotNull();

            foreach (var action in new[] { TableMaintenanceAction.Truncate, TableMaintenanceAction.Rename, TableMaintenanceAction.Drop })
            {
                await vm.RunTableMaintenanceAsync(Customers, action);
                await Assert.That(window.OwnedWindows).IsEmpty();
                await Assert.That(vm.ActiveTab.Status).Contains(vm.ConnectionReadOnlyHint!);
            }

            // The server allows VACUUM and ANALYZE on a read-only session.
            await Assert.That(vm.CanToggleWrites).IsFalse();
            await vm.RunTableMaintenanceAsync(Customers, TableMaintenanceAction.Vacuum);
            await vm.RunTableMaintenanceAsync(Customers, TableMaintenanceAction.Analyze);
            await Assert.That(sent).IsEquivalentTo(["VACUUM \"public\".\"customers\"", "ANALYZE \"public\".\"customers\""]);

            window.Close();
        });
    }

    [Test]
    public async Task A_production_window_with_writes_allowed_runs_all_five()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell(environment: ConnectionEnvironment.Production);
            vm.ConfirmAllowWritesRequested = () => Task.FromResult(true);
            vm.WriteStateProbe = _ => Task.FromResult(
                vm.WritesAllowed ? SessionWriteState.ReadWrite : SessionWriteState.ReadOnly);
            await vm.AllowWritesCommand.ExecuteAsync(null);
            await Assert.That(vm.WritesAllowed).IsTrue();
            await Assert.That(vm.ConnectionReadOnlyHint).IsNull();

            vm.ConfirmMaintenanceRequested = (_, _) => Task.FromResult(true);
            vm.RenameRelationRequested = (_, _) => Task.FromResult<string?>("clients");

            // Not awaited: after Rename and Drop the schema refresh waits on the
            // fixture's unroutable data source.
            foreach (var action in Enum.GetValues<TableMaintenanceAction>())
            {
                _ = vm.RunTableMaintenanceAsync(Customers, action);
                Ui.Settle();
            }

            Ui.SettleUntil(() => sent.Count == 5);
            await Assert.That(sent).IsEquivalentTo([
                "ANALYZE \"public\".\"customers\"",
                "VACUUM \"public\".\"customers\"",
                "TRUNCATE TABLE \"public\".\"customers\"",
                "ALTER TABLE \"public\".\"customers\" RENAME TO \"clients\"",
                "DROP TABLE \"public\".\"customers\"",
            ]);
            window.Close();
        });
    }

    [Test]
    [Arguments(RelationKind.Table, TableMaintenanceAction.Truncate, "Truncate table \"public.r\" in database \"shop\"? Every row in it is deleted, and this can't be undone.")]
    [Arguments(RelationKind.PartitionedTable, TableMaintenanceAction.Truncate, "Truncate table \"public.r\" in database \"shop\"? Every row in it and in all its partitions is deleted, and this can't be undone.")]
    [Arguments(RelationKind.Table, TableMaintenanceAction.Drop, "Drop table \"public.r\" in database \"shop\"? The table and every row in it are removed, and this can't be undone.")]
    [Arguments(RelationKind.PartitionedTable, TableMaintenanceAction.Drop, "Drop table \"public.r\" in database \"shop\"? The table and all its partitions, with every row in them, are removed, and this can't be undone.")]
    [Arguments(RelationKind.MaterializedView, TableMaintenanceAction.Drop, "Drop materialized view \"public.r\" in database \"shop\"? The materialized view and its data are removed, and this can't be undone.")]
    [Arguments(RelationKind.View, TableMaintenanceAction.Drop, "Drop view \"public.r\" in database \"shop\"? The view is removed, and this can't be undone.")]
    public async Task The_confirm_says_what_is_lost_for_each_kind(RelationKind kind, TableMaintenanceAction action, string start)
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell();
            string? message = null;
            vm.ConfirmMaintenanceRequested = (text, _) =>
            {
                message = text;
                return Task.FromResult(false);
            };

            await vm.RunTableMaintenanceAsync(new("public", "r", kind), action);

            await Assert.That(message).StartsWith(start);
            await Assert.That(sent).IsEmpty();
            window.Close();
        });
    }

    [Test]
    public async Task A_production_window_with_writes_off_runs_none_of_them()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell(environment: ConnectionEnvironment.Production);

            foreach (var action in Enum.GetValues<TableMaintenanceAction>())
            {
                await vm.RunTableMaintenanceAsync(Customers, action);
                await Assert.That(window.OwnedWindows).IsEmpty();
                await Assert.That(vm.ActiveTab.Status).Contains(MainViewModel.ProductionLockedHint);
            }

            await Assert.That(sent).IsEmpty();
            window.Close();
        });
    }

    [Test]
    public async Task The_palette_lists_the_actions_only_for_a_target_and_its_kind()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, _) = Shell();
            string[] all = ["Analyze table", "Vacuum table", "Truncate table…", "Rename table…", "Drop table…"];

            // Nothing selected in the tree and no browsed table: none of them.
            var titles = PaletteTitles(vm, "table");
            foreach (var title in all)
            {
                await Assert.That(titles).DoesNotContain(title);
            }

            var nodes = vm.SchemaTree.Schemas.SelectMany(s => s.Children).OfType<TableNode>().ToList();
            vm.SchemaTree.SelectedNode = nodes.Single(n => n.Name == "customers");
            titles = PaletteTitles(vm, "table");
            foreach (var title in all)
            {
                await Assert.That(titles).Contains(title);
            }

            vm.SchemaTree.SelectedNode = nodes.Single(n => n.Name == "active_customers");
            titles = PaletteTitles(vm, "table");
            await Assert.That(titles).DoesNotContain("Analyze table");
            await Assert.That(titles).DoesNotContain("Vacuum table");
            await Assert.That(titles).DoesNotContain("Truncate table…");
            await Assert.That(titles).Contains("Rename table…");
            await Assert.That(titles).Contains("Drop table…");

            window.Close();
        });
    }

    [Test]
    public async Task A_palette_row_acts_on_the_relation_selected_in_the_tree()
    {
        await Ui.Run(async () =>
        {
            var (window, vm, sent) = Shell();
            vm.SchemaTree.SelectedNode = vm.SchemaTree.Schemas.SelectMany(s => s.Children).OfType<TableNode>()
                .Single(n => n.Name == "daily_revenue");

            await vm.AnalyzeTableCommand.ExecuteAsync(null);

            await Assert.That(sent).IsEquivalentTo(["ANALYZE \"public\".\"daily_revenue\""]);
            window.Close();
        });
    }

    private static List<string> PaletteTitles(MainViewModel vm, string search)
    {
        // Fire and forget: the table half of the palette waits on a catalog
        // fetch that never answers against the fixture data source.
        _ = vm.OpenCommandPaletteAsync();
        Ui.Settle();
        vm.CommandPalette.SearchText = search;
        Ui.Settle();
        var titles = vm.CommandPalette.Results.Select(i => i.Title).ToList();
        vm.CommandPalette.CloseCommand.Execute(null);
        Ui.Settle();
        return titles;
    }
}
