using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Views;

/// <summary>
/// Live pg_stat_activity view. The 2-second auto-refresh timer runs only while
/// the window is open (and pauses when the Auto toggle is off), so a forgotten
/// window never keeps polling the server.
/// </summary>
public partial class ActivityWindow : Window
{
    private readonly DispatcherTimer _timer;

    public ActivityWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            if (DataContext is ActivityViewModel { AutoRefresh: true } vm && !vm.RefreshCommand.IsRunning)
            {
                vm.RefreshCommand.Execute(null);
            }
        };

        Opened += (_, _) =>
        {
            (DataContext as ActivityViewModel)?.RefreshCommand.Execute(null);
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Terminate whatever the visible tab has selected — the view model resolves the target.</summary>
    private async void OnTerminateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ActivityViewModel { TargetPid: { } pid } vm)
        {
            return;
        }

        var confirm = new ConfirmDialog(Strings.Monitoring_ActivityTerminateConfirm(pid, vm.TargetLabel), Strings.Monitoring_ActivityTerminateButton);
        if (await confirm.ShowDialog<bool>(this))
        {
            await vm.TerminateCommand.ExecuteAsync(null);
        }
    }
}
