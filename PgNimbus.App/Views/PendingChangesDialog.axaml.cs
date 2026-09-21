using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PgNimbus.App.Views;

/// <summary>
/// Safe mode's pre-commit review: shows the staged change set as a readable
/// SQL script, then commits it all as one transaction, discards it all, or
/// does nothing. Shown via <c>ShowDialog&lt;PendingChangesDialog.Result&gt;</c>;
/// closing the window without choosing counts as Cancel.
/// </summary>
public partial class PendingChangesDialog : Window
{
    public enum Result
    {
        Cancel,
        Commit,
        Discard,
    }

    public PendingChangesDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    public PendingChangesDialog(string summary, string sqlScript, int changeCount, IReadOnlyList<string>? uncheckedColumns = null) : this()
    {
        SummaryText.Text = summary;
        SqlText.Text = sqlScript;
        CommitButton.Content = changeCount == 1 ? "Commit 1 change" : $"Commit {changeCount} changes";

        // "Checked" must never quietly mean "partly checked": name the columns
        // the concurrency check can't compare.
        if (uncheckedColumns is { Count: > 0 })
        {
            UncheckedText.Text = $"Not checked for changes by other sessions (pgNimbus can't read their type): {string.Join(", ", uncheckedColumns)}.";
            UncheckedText.IsVisible = true;
        }
    }

    private void OnCommitClick(object? sender, RoutedEventArgs e) => Close(Result.Commit);

    private void OnDiscardClick(object? sender, RoutedEventArgs e) => Close(Result.Discard);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(Result.Cancel);
}
