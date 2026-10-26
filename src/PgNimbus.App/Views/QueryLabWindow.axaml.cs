using Avalonia.Controls;
using Avalonia.Input;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;

namespace PgNimbus.App.Views;

/// <summary>
/// Query Lab: the saved plans on the left, a comparison of two on the right. A reference
/// window like Slow Queries, read beside the query being tuned; nothing runs from it.
/// It owns the dialogs its view model asks for (import, name, delete), so they open over it.
/// </summary>
public partial class QueryLabWindow : Window
{
    public QueryLabWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        Opened += (_, _) =>
        {
            if (ViewModel is not { } model)
            {
                return;
            }

            model.PickImport = PickImportAsync;
            model.AskName = AskNameAsync;
            model.ConfirmDelete = ConfirmDeleteAsync;
            SavedPlanStore.Changed += OnStoreChanged;
            _ = model.ReloadAsync();
        };
        Closed += (_, _) => SavedPlanStore.Changed -= OnStoreChanged;

        // Double-click opens the plan in a new tab: the list's default action.
        PlansList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: SavedPlanItem item })
            {
                ViewModel?.OpenInNewTabCommand.Execute(item);
            }
        };
        PlansList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && ViewModel is { SelectedPlan: { } item } model)
            {
                e.Handled = true;
                model.DeleteCommand.Execute(item);
            }
        };
    }

    private QueryLabViewModel? ViewModel => DataContext as QueryLabViewModel;

    // Another window's lab saved, renamed or deleted a plan; the store raises this on that thread.
    private void OnStoreChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ViewModel?.ReloadAsync());

    private Task<ImportedPlan?> PickImportAsync() => new ImportPlanDialog().ShowDialog<ImportedPlan?>(this);

    private async Task<(string Name, Guid? ReplaceId)?> AskNameAsync(string heading, string initialName, Guid? currentId)
    {
        var model = ViewModel;
        var dialog = new SaveQueryDialog(
            heading,
            initialName,
            currentId,
            name => model?.FindByName(name) is { } plan ? (plan.Id, plan.Name) : null,
            "saved plan",
            "pgNimbus — Save plan");
        return await dialog.ShowDialog<SaveQueryResult?>(this) is { } result ? (result.Name, result.OverwriteId) : null;
    }

    private Task<bool> ConfirmDeleteAsync(SavedPlan plan) =>
        new ConfirmDialog(
                $"Delete the saved plan “{plan.Name}” from Query Lab? A tab showing it keeps it until the tab closes.",
                "Delete")
            .ShowDialog<bool>(this);
}
