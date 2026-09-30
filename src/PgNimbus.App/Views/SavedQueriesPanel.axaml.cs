using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

/// <summary>Sidebar panel for saved queries and run history. Binds to a
/// <see cref="SavedQueriesViewModel"/> supplied as its DataContext by the host
/// window. Double-tapping a saved query or history row opens it in a new tab.
/// <para>
/// Saving is deliberately <em>not</em> here anymore: it starts at the tab being
/// saved (right-click it, or the Save shortcut) and this panel is the library
/// you read back. The list's own verbs — open, rename, delete — are on its
/// right-click menu rather than a button row, which is what stopped "Save" from
/// reading as a fourth verb acting on the list selection. The history list went
/// the same way: open, copy, pin and clear are its right-click menu.
/// </para></summary>
public partial class SavedQueriesPanel : UserControl
{
    public SavedQueriesPanel()
    {
        InitializeComponent();

        SavedQueriesList.DoubleTapped += (_, e) => OnQueryListDoubleTapped(e,
            item => Model?.LoadSavedQueryCommand.Execute(item as SavedQuery));
        HistoryList.DoubleTapped += (_, e) => OnQueryListDoubleTapped(e,
            item => Model?.LoadHistoryEntryCommand.Execute(item as QueryHistoryEntry));
    }

    private SavedQueriesViewModel? Model => DataContext as SavedQueriesViewModel;

    // The flyout's items act on the row that was right-clicked, which the
    // ListBox has already made its selection by the time a click lands.
    private SavedQuery? Selected => SavedQueriesList.SelectedItem as SavedQuery;

    private void OnQueryListDoubleTapped(TappedEventArgs e, Action<object?> load)
    {
        var source = e.Source as Visual;
        if (source?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (source?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } item })
        {
            load(item);
        }
    }

    private void OnOpenSavedQueryClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } query)
        {
            Model?.LoadSavedQueryCommand.Execute(query);
        }
    }

    private void OnDeleteSavedQueryClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } query)
        {
            Model?.DeleteSavedQueryCommand.Execute(query);
        }
    }

    private QueryHistoryEntry? SelectedHistoryEntry => HistoryList.SelectedItem as QueryHistoryEntry;

    // Pin and Unpin are one item whose header follows the entry right-clicked.
    private void OnHistoryMenuOpening(object? sender, EventArgs e) =>
        PinHistoryMenuItem.Header = SelectedHistoryEntry is { Pinned: true } ? "Unpin" : "Pin";

    private void OnOpenHistoryEntryClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedHistoryEntry is { } entry)
        {
            Model?.LoadHistoryEntryCommand.Execute(entry);
        }
    }

    private void OnTogglePinHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedHistoryEntry is { } entry)
        {
            Model?.TogglePinCommand.Execute(entry);
        }
    }

    private async void OnCopyHistorySqlClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedHistoryEntry is not { } entry || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(entry.Sql);
        }
        catch (Exception)
        {
            // Clipboard access can throw if another app holds it locked; a
            // failed copy is not worth surfacing, let alone crashing over.
        }
    }

    private void OnRenameSavedQueryClick(object? sender, RoutedEventArgs e) => _ = RenameSelectedAsync();

    /// <summary>
    /// Renames through the same modal that named the query in the first place,
    /// so the name-already-taken check is stated once and a rename cannot walk
    /// the list into the duplicate-names state saving is now careful to avoid.
    /// </summary>
    private async System.Threading.Tasks.Task RenameSelectedAsync()
    {
        if (Model is not { } model || Selected is not { } query
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new SaveQueryDialog("Rename saved query", query.Name, query.Id, model.FindByName);
        if (await dialog.ShowDialog<SaveQueryResult?>(owner) is { } result)
        {
            model.RenameSavedQuery(query, result.Name);
        }
    }
}
