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

        Opened += (_, _) => (DataContext as DatabaseOverviewViewModel)?.RefreshCommand.Execute(null);
    }
}
