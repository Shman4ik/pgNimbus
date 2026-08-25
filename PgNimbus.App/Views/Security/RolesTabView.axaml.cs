using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
        RolesGrid.ContextRequested += OnRolesGridContextRequested;
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

    /// <summary>
    /// The right-click menu, built in code rather than as a XAML
    /// <c>ContextFlyout</c> for the same two reasons the tab strip's is: the
    /// handler has to re-target the selection before the menu opens, and a
    /// flyout on the grid itself would also fire on its empty space.
    ///
    /// Four items, each one a route to something the button row already does
    /// (UI design rule 1 — a context menu is not a dumping ground either). The
    /// odd one out is Copy name, which earns its place because every one of
    /// these roles ends up typed into a GRANT sooner or later.
    /// </summary>
    private void OnRolesGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not RolesTabViewModel vm
            || (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not { } row
            || row.DataContext is not RoleRowViewModel role)
        {
            return; // right-click on the header or the empty space below the rows
        }

        // Right-click targets what it points at, the way VS and Notepad++ do, so
        // the menu's verbs read against the role the user is looking at.
        vm.SelectedRole = role;

        _roleMenu ??= BuildRoleMenu(vm);
        _roleMenu.ShowAt(row, showAtPointer: true);
        e.Handled = true;
    }

    private MenuFlyout? _roleMenu;

    private MenuFlyout BuildRoleMenu(RolesTabViewModel vm)
    {
        var copyName = new MenuItem { Header = "Copy name" };
        copyName.Click += OnCopyRoleNameClick;

        return new MenuFlyout
        {
            Items =
            {
                new MenuItem { Header = "Edit…", Command = vm.EditRoleCommand },
                copyName,
                new MenuItem { Header = "CREATE ROLE script", Command = vm.CopyCreateScriptCommand },
                new Separator(),
                new MenuItem { Header = "Drop role…", Command = vm.DropRoleCommand },
            },
        };
    }

    private async void OnCopyRoleNameClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RolesTabViewModel { SelectedRole: { } role }
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(role.Name);
        }
        catch (Exception)
        {
            // Clipboard access can throw if another app holds it locked; a failed
            // copy is not worth surfacing, let alone crashing over.
        }
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
