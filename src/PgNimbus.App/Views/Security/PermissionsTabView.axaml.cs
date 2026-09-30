using Avalonia.Controls;
using PgNimbus.App.ViewModels.Security;

namespace PgNimbus.App.Views.Security;

/// <summary>
/// The permissions matrix. The view owns the bulk-grant modal; the view model
/// asks for it through a callback rather than constructing a
/// <see cref="Window"/> itself.
/// </summary>
public partial class PermissionsTabView : UserControl
{
    public PermissionsTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is PermissionsTabViewModel vm)
        {
            vm.ShowBulkGrantDialog = ShowBulkGrantAsync;
        }
    }

    private async Task<bool> ShowBulkGrantAsync(BulkGrantViewModel model)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        var dialog = new BulkGrantDialog { DataContext = model };
        return await dialog.ShowDialog<bool>(owner);
    }
}
