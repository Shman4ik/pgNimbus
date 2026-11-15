using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Views;

/// <summary>
/// The database list in the title bar's flyout (UI rule 7: its own control,
/// bound to <see cref="DatabaseSwitcherViewModel"/>). The filter box keeps focus
/// and drives the list, as the tab finder's does.
/// </summary>
public partial class DatabaseSwitcherView : UserControl
{
    public DatabaseSwitcherView()
    {
        InitializeComponent();
        FilterBox.KeyDown += OnFilterKeyDown;
        DatabaseList.Tapped += OnListTapped;
        DatabaseList.ContextRequested += OnListContextRequested;
    }

    private DatabaseSwitcherViewModel? Model => DataContext as DatabaseSwitcherViewModel;

    /// <summary>Puts the caret in the filter box, after the flyout has finished opening (it takes focus itself).</summary>
    public void FocusFilter() => Dispatcher.UIThread.Post(() => FilterBox.Focus());

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                model.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                model.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = model.ChooseAsync();
                e.Handled = true;
                break;
        }
    }

    // Only a tap on a row opens it; one on the scrollbar or empty space must not.
    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (Model is { } model
            && e.Source is Avalonia.Visual v
            && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: DatabaseSwitcherItem item })
        {
            _ = model.ChooseAsync(item);
        }
    }

    // One item, Open in New Window, acting on the row it was opened on (which is
    // selected first, so the highlight says which). Built here rather than in the
    // item template so a window with no new-window path offers no menu at all.
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Model is not { } model
            || e.Source is not Avalonia.Visual v
            || v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: DatabaseSwitcherItem item } row)
        {
            return;
        }

        e.Handled = true;
        model.Selected = item;
        if (!model.CanOpenInNewWindow)
        {
            return;
        }

        var open = new MenuItem { Header = Strings.Shell_OpenInNewWindow };
        open.Click += (_, _) => _ = model.OpenInNewWindowAsync(item);
        RowMenu = new ContextMenu { ItemsSource = new[] { open } };
        RowMenu.Open(row);
    }

    /// <summary>The row menu last opened; the tests click its item.</summary>
    public ContextMenu? RowMenu { get; private set; }
}
