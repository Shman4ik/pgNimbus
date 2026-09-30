using Avalonia.Controls;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Security audit 2026-09, finding 6: "Drop column" ran straight from the
/// dialog's command with nothing in between, one click after selecting a row.
/// It now confirms first, the same way as drop schema, drop extension, delete
/// rows and terminate backend — see <see cref="AlterTableViewModel.ConfirmDropColumnRequested"/>
/// and <see cref="AlterTableDialog"/>.
/// </summary>
public class AlterTableConfirmTests
{
    private static AlterTableViewModel BuildViewModel(out ColumnDetail column)
    {
        var vm = new AlterTableViewModel(new SchemaEditor(Fixtures.DataSource), new SchemaService(Fixtures.DataSource), "public", "widgets");
        column = new ColumnDetail("name", "text", false, false);
        vm.Columns.Add(column);
        vm.SelectedColumn = column;
        return vm;
    }

    [Test]
    public async Task Declining_the_confirm_leaves_the_column_untouched()
    {
        await Ui.Run(async () =>
        {
            var vm = BuildViewModel(out _);
            vm.ConfirmDropColumnRequested = _ => Task.FromResult(false);

            await vm.DropColumnCommand.ExecuteAsync(null);

            // A decline returns before IsBusy is ever set, so the drop never
            // reaches the SchemaEditor (which would otherwise hang forever
            // against the fixture's unroutable address).
            await Assert.That(vm.IsBusy).IsFalse();
            await Assert.That(vm.Columns.Count).IsEqualTo(1);
            await Assert.That(vm.SelectedColumn).IsNotNull();
        });
    }

    [Test]
    public async Task Accepting_the_confirm_issues_the_drop()
    {
        await Ui.Run(async () =>
        {
            var vm = BuildViewModel(out _);
            vm.ConfirmDropColumnRequested = _ => Task.FromResult(true);

            // Not awaited, the same reason as OpenCommandPaletteAsync in
            // ShellTests: past the confirm, DropColumnAsync calls the real
            // SchemaEditor against the fixture data source, which points at
            // an unroutable address (TEST-NET-3) that never answers. IsBusy
            // going true and staying true is the proof the drop was actually
            // issued, not just accepted.
            _ = vm.DropColumnCommand.ExecuteAsync(null);
            Ui.SettleUntil(() => vm.IsBusy);

            await Assert.That(vm.IsBusy).IsTrue();
        });
    }

    /// <summary>
    /// The dialog wires the confirm to a real <see cref="ConfirmDialog"/>
    /// naming <c>schema.table.column</c>, with the danger-styled affirmative
    /// that is never the window's default button (CLAUDE.md UI design rule 6).
    /// </summary>
    [Test]
    public async Task The_dialog_confirms_through_a_real_ConfirmDialog()
    {
        await Ui.Run(async () =>
        {
            var vm = BuildViewModel(out var column);

            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);

            var dialog = new AlterTableDialog { DataContext = vm };
            // Not awaited: Opened also kicks off LoadAsync against the same
            // unroutable fixture data source, which never returns either.
            _ = dialog.ShowDialog(owner);
            Ui.Settle();

            await Assert.That(vm.ConfirmDropColumnRequested).IsNotNull();

            var confirmTask = vm.ConfirmDropColumnRequested!(column);
            Ui.Settle();

            var confirm = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            await Assert.That(confirm.FindControl<TextBlock>("MessageText")!.Text)
                .IsEqualTo("Drop column \"public.widgets.name\"? Its data is lost.");

            var confirmButton = confirm.FindControl<Button>("ConfirmButton")!;
            await Assert.That((string?)confirmButton.Content).IsEqualTo("Drop");
            await Assert.That(confirmButton.Classes.Contains("danger")).IsTrue();
            await Assert.That(confirmButton.IsDefault).IsFalse();

            // Decline, as a click on Cancel would (ConfirmDialog.OnCancelClick
            // does exactly this: Close(false)).
            confirm.Close(false);
            Ui.Settle();

            await Assert.That(await confirmTask).IsFalse();

            dialog.Close();
            owner.Close();
        });
    }
}
