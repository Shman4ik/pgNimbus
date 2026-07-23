using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

/// <summary>
/// Modal for pasting an externally-produced EXPLAIN plan (JSON or text). Parses on
/// Import via <see cref="ExplainService.Import"/>; a parse failure is shown inline and
/// the dialog stays open. Returns the parsed <see cref="ImportedPlan"/> (or null when
/// cancelled) via <c>ShowDialog&lt;ImportedPlan?&gt;</c>.
/// </summary>
public partial class ImportPlanDialog : Window
{
    public ImportPlanDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
        Opened += (_, _) => PlanInput.Focus();
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var imported = ExplainService.Import(PlanInput.Text ?? string.Empty);
            Close(imported);
        }
        catch (FormatException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
