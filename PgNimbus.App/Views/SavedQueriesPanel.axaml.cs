using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

/// <summary>Sidebar panel for saved queries and run history. Binds to a
/// <see cref="SavedQueriesViewModel"/> supplied as its DataContext by the host
/// window. Double-tapping a saved query or history row opens it in a new tab —
/// the same action as its Load button.</summary>
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
}
