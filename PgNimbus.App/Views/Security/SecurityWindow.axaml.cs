using Avalonia.Controls;
using PgNimbus.App.ViewModels.Security;

namespace PgNimbus.App.Views.Security;

/// <summary>
/// The Roles &amp; Permissions window: who exists, what they can do, and which
/// grant explains it. A reference view you keep open beside the editor while you
/// fix something, which is why it is a window rather than an overlay panel (see
/// the OverlayPanel rule in CLAUDE.md) — and why the scripts it generates land
/// in the main window's editor rather than being applied from here.
///
/// One live instance, opened from the command palette, no toolbar button. Same
/// shape as <c>DatabaseOverviewWindow</c>, including the snapshot-on-open.
/// </summary>
public partial class SecurityWindow : Window
{
    public SecurityWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        Opened += (_, _) => (DataContext as SecurityViewModel)?.RefreshCommand.Execute(null);
    }
}
