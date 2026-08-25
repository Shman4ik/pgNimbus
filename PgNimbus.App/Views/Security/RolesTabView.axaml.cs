using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels.Security;

namespace PgNimbus.App.Views.Security;

/// <summary>
/// The roles list and one role's detail. The view owns the two modal dialogs
/// the tab can raise: the view model asks for one through a callback and never
/// constructs a <see cref="Window"/> itself, which is how the rest of this
/// codebase splits the two (see <c>SchemaTreeViewModel.AlterTableViewModelFactory</c>).
/// </summary>
public partial class RolesTabView : UserControl
{
    public RolesTabView()
    {
        InitializeComponent();

        // The DataContext arrives after construction (the TabItem binds it), and
        // can in principle be re-pointed, so the callbacks are wired on change
        // rather than once in the constructor.
        DataContextChanged += OnDataContextChanged;
        RolesGrid.DoubleTapped += OnRolesGridDoubleTapped;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not RolesTabViewModel vm)
        {
            return;
        }

        vm.ShowRoleDialog = editor => ShowDialogAsync(new RoleDialog { DataContext = editor }, editor);
        vm.ShowDropDialog = drop => ShowDialogAsync(new DropRoleDialog { DataContext = drop }, drop);
    }

    /// <summary>
    /// Shows a modal whose view model closes it through a <c>CloseRequested</c>
    /// callback, and reports whether anything was applied — the tab refreshes
    /// only on true, so a cancelled dialog costs no catalog round trip.
    /// </summary>
    private async Task<bool> ShowDialogAsync(Window dialog, object viewModel)
    {
        switch (viewModel)
        {
            case RoleEditorViewModel editor:
                editor.CloseRequested = result => dialog.Close(result);
                break;
            case DropRoleViewModel drop:
                drop.CloseRequested = result => dialog.Close(result);
                // The dependency lists are a catalog read, so they load once the
                // dialog is up rather than blocking the click that opened it.
                dialog.Opened += async (_, _) => await drop.LoadAsync(CancellationToken.None);
                break;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        return owner is null ? false : await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>Double-click performs the default action for a role — edit it (UI design rule 2).</summary>
    private void OnRolesGridDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RolesTabViewModel { EditRoleCommand: { } edit } && edit.CanExecute(null))
        {
            edit.Execute(null);
        }
    }
}
