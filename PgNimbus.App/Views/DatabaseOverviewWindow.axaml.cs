using Avalonia.Controls;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// Read-only database statistics (size, cache-hit ratios, largest relations,
/// scan usage, unused indexes). Unlike the activity view these don't move
/// second to second, so there's no auto-refresh timer — the window snapshots
/// once on open and re-snapshots only when the user hits Refresh.
/// </summary>
public partial class DatabaseOverviewWindow : Window
{
    public DatabaseOverviewWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        // Snapshot on open, but only when there is nothing to show yet: a view
        // model handed over with a snapshot already in it (restored state, the
        // screenshot harness) would otherwise have it blanked out by a refresh
        // nobody asked for.
        Opened += (_, _) =>
        {
            if (DataContext is DatabaseOverviewViewModel { HasSnapshot: false } vm)
            {
                vm.RefreshCommand.Execute(null);
            }
        };
    }
}
