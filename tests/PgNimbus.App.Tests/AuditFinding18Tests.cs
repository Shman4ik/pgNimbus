using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.Platform;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The UI halves of security audit 2026-09, finding 18: the spreadsheet-safe
/// option reaches the grid's copy, the connection string's everyday copy has
/// no password while the full one is marked secret and cleared, and add-row
/// fields cast to the schema-qualified type.
/// </summary>
[NotInParallel]
public class AuditFinding18Tests
{
    private static void SeedFormulaResult(MainViewModel vm) =>
        vm.ActiveTab.SeedResult(
            [new ColumnInfo("amount", "integer", typeof(int)), new ColumnInfo("note", "text", typeof(string))],
            [[-5, "=HYPERLINK(\"http://x\",\"y\")"], [7, "plain"]]);

    [Test]
    public async Task Grid_copy_neutralizes_formulas_only_when_the_option_is_on()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            SeedFormulaResult(vm);
            Ui.Settle();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First();

            async Task<string?> CopyAll()
            {
                grid.Focus();
                Ui.Settle();
                EditCommands.Execute(window, EditCommand.SelectAll);
                Ui.Settle();
                EditCommands.Execute(window, EditCommand.Copy);
                Ui.Settle();
                return await window.Clipboard!.TryGetTextAsync();
            }

            await Assert.That(vm.SpreadsheetSafeExport).IsFalse();
            await Assert.That(await CopyAll()).Contains("-5\t=HYPERLINK");

            vm.SpreadsheetSafeExport = true;
            var safe = await CopyAll();
            await Assert.That(safe).Contains("-5\t'=HYPERLINK");
            await Assert.That(safe).Contains("7\tplain");

            window.Close();
        });
    }

    [Test]
    public async Task The_export_menu_carries_the_spreadsheet_option_bound_to_the_setting()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            var exportButton = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Flyout is MenuFlyout flyout && flyout.Items.OfType<MenuItem>().Any(m => m.Name == "SpreadsheetSafeMenuItem"));
            var flyout = (MenuFlyout)exportButton.Flyout!;
            flyout.ShowAt(exportButton);
            Ui.Settle();
            var item = flyout.Items.OfType<MenuItem>().Single(m => m.Name == "SpreadsheetSafeMenuItem");

            await Assert.That(item.ToggleType).IsEqualTo(MenuItemToggleType.CheckBox);
            await Assert.That(item.IsChecked).IsFalse();
            vm.SpreadsheetSafeExport = true;
            Ui.Settle();
            await Assert.That(item.IsChecked).IsTrue();

            flyout.Hide();
            window.Close();
        });
    }

    [Test]
    public async Task The_everyday_connection_string_copy_has_no_password()
    {
        await Ui.Run(async () =>
        {
            var window = (ConnectionDialog)Scenarios.ConnectionDialog();
            var vm = (ConnectionDialogViewModel)window.DataContext!;
            Ui.Show(window);
            vm.Password = "s3cret-pw";
            Ui.Settle();

            await Assert.That(vm.BuildClipboardConnectionString()).DoesNotContain("s3cret-pw");
            await Assert.That(vm.BuildClipboardConnectionString(includePassword: true)).Contains("s3cret-pw");

            var button = window.FindControl<Button>("CopyConnectionStringButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Ui.Settle();
            var copied = await window.Clipboard!.TryGetTextAsync();
            await Assert.That(copied).IsNotNull();
            await Assert.That(copied!).StartsWith("postgres://");
            await Assert.That(copied).DoesNotContain("s3cret-pw");

            // The copy with the password is on the right-click menu, not a second button.
            var menu = button.ContextMenu!;
            var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string ?? "").ToList();
            await Assert.That(headers).IsEquivalentTo(new[] { "Copy Without Password", "Copy With Password" });

            await vm.FlushAsync();
            window.Close();
        });
    }

    [Test]
    public async Task Copy_with_password_marks_the_text_secret_and_clears_it_later()
    {
        var previous = SecretClipboard.ClearAfter;
        SecretClipboard.ClearAfter = TimeSpan.FromMilliseconds(300);
        try
        {
            await Ui.Run(async () =>
            {
                var window = (ConnectionDialog)Scenarios.ConnectionDialog();
                var vm = (ConnectionDialogViewModel)window.DataContext!;
                Ui.Show(window);
                vm.Password = "s3cret-pw";
                Ui.Settle();

                var item = window.FindControl<Button>("CopyConnectionStringButton")!.ContextMenu!
                    .Items.OfType<MenuItem>().Single(m => m.Name == "CopyWithPasswordMenuItem");
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Ui.Settle();

                var clipboard = window.Clipboard!;
                await Assert.That(await clipboard.TryGetTextAsync()).Contains("s3cret-pw");
                var formats = (await clipboard.GetDataFormatsAsync()).Select(f => f.Identifier).ToList();
                foreach (var (marker, _) in SecretClipboard.Markers)
                {
                    await Assert.That(formats).Contains(marker);
                }

                await Task.Delay(800);
                Ui.Settle();
                await Assert.That(await clipboard.TryGetTextAsync()).IsNull();

                await vm.FlushAsync();
                window.Close();
            });
        }
        finally
        {
            SecretClipboard.ClearAfter = previous;
        }
    }

    [Test]
    public async Task A_secret_copy_is_not_cleared_once_something_else_was_copied()
    {
        var previous = SecretClipboard.ClearAfter;
        SecretClipboard.ClearAfter = TimeSpan.FromMilliseconds(300);
        try
        {
            await Ui.Run(async () =>
            {
                var window = (ConnectionDialog)Scenarios.ConnectionDialog();
                Ui.Show(window);
                var clipboard = window.Clipboard!;

                await SecretClipboard.SetAsync(clipboard, "postgres://u:pw@h/db");
                await clipboard.SetTextAsync("something the user copied since");
                await Task.Delay(800);
                Ui.Settle();

                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("something the user copied since");
                window.Close();
            });
        }
        finally
        {
            SecretClipboard.ClearAfter = previous;
        }
    }

    [Test]
    public async Task Add_row_fields_cast_to_the_qualified_type()
    {
        var column = new ColumnDetail("feeling", "mood", NotNull: false, IsPrimaryKey: false)
        {
            Editor = ColumnValueEditor.Enum,
            QualifiedDataType = "public.mood",
        };

        var field = NewRowField.For(column);

        await Assert.That(field.DataType).IsEqualTo("mood");
        await Assert.That(field.CastType).IsEqualTo("public.mood");
        await Assert.That(NewRowField.For(column with { QualifiedDataType = null }).CastType).IsEqualTo("mood");
    }
}
