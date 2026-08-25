using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PgNimbus.App.Views.Security;

/// <summary>
/// Shown via <c>ShowDialog&lt;bool&gt;</c> by <see cref="RolesTabView"/>. The view
/// model closes it through its <c>CloseRequested</c> callback, so an error from
/// the server keeps the dialog up with its message rather than dismissing it —
/// on a managed server that message ("permission denied to create role") is the
/// useful part.
/// </summary>
public partial class DropRoleDialog : Window
{
    public DropRoleDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
