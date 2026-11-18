using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using PgNimbus.Core.Localization;

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
        DialogChrome.Attach(this);
    }

    public PendingChangesDialog(string summary, string sqlScript, int changeCount, IReadOnlyList<string>? uncheckedColumns = null) : this()
    {
        SummaryText.Text = summary;
        SqlText.Text = sqlScript;
        CommitButton.Bind(ContentControl.ContentProperty, Localizer.Observe(() => Strings.Dialogs_CommitChanges(changeCount)).ToBinding());

        // "Checked" must never quietly mean "partly checked": name the columns
        // the concurrency check can't compare.
        if (uncheckedColumns is { Count: > 0 })
        {
            var columns = string.Join(", ", uncheckedColumns);
            UncheckedText.Bind(TextBlock.TextProperty, Localizer.Observe(() => Strings.Dialogs_PendingUnchecked(columns)).ToBinding());
            UncheckedText.IsVisible = true;
        }
    }

    private void OnCommitClick(object? sender, RoutedEventArgs e) => Close(Result.Commit);

    private void OnDiscardClick(object? sender, RoutedEventArgs e) => Close(Result.Discard);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(Result.Cancel);
}
