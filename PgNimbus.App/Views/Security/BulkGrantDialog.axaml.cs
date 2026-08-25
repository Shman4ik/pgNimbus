using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PgNimbus.App.Views.Security;

/// <summary>
/// "Give this role read access to this schema", as a script. The form edits a
/// <c>BulkGrantRequest</c> and the preview re-renders live; the affirmative is
/// <b>Open in editor</b>, which hands the script to the main window's editor
/// through the caller.
///
/// There is deliberately no Apply. Every privilege change in this feature leaves
/// as SQL the user can read, edit and keep — this dialog never writes to the
/// database, so it returns nothing but "the user accepted".
/// </summary>
public partial class BulkGrantDialog : Window
{
    public BulkGrantDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
