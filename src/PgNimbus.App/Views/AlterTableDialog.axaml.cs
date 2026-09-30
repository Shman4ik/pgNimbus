using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

public partial class AlterTableDialog : Window
{
    public AlterTableDialog()
    {
        InitializeComponent();
        DialogChrome.Attach(this);

        Opened += async (_, _) =>
        {
            if (DataContext is AlterTableViewModel vm)
            {
                // One click after selecting a row used to destroy the column's
                // data with nothing in between (security audit 2026-09, finding
                // 6). Confirmed the same way as drop schema/extension: the view
                // shows ConfirmDialog with this window as owner, naming
                // schema.table.column so there's no doubt what's about to go.
                vm.ConfirmDropColumnRequested = async column =>
                {
                    var confirm = new ConfirmDialog(
                        $"Drop column \"{vm.Schema}.{vm.Table}.{column.Name}\"? Its data is lost.",
                        "Drop");
                    return await confirm.ShowDialog<bool>(this);
                };

                await vm.LoadAsync();
            }
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
