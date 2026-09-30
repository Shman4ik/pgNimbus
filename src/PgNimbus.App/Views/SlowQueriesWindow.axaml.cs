using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The slow-query shortlist (pg_stat_statements). Reads once on open, like the
/// Database Overview, and again on Refresh. The first read is the baseline the
/// "Since …" scope counts from.
/// </summary>
public partial class SlowQueriesWindow : Window
{
    public SlowQueriesWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        Opened += (_, _) => ViewModel?.RefreshCommand.Execute(null);

        // Double-click opens the statement in a new tab: the list's default action.
        StatementsGrid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: SlowQueryRow row })
            {
                ViewModel?.OpenInNewTabCommand.Execute(row);
            }
        };
    }

    private SlowQueriesViewModel? ViewModel => DataContext as SlowQueriesViewModel;

    // The whole statement, not the one-line preview the grid shows.
    private async void OnCopyQueryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { SelectedRow: { } row } vm && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(await vm.GetQueryTextAsync(row));
        }
    }
}
