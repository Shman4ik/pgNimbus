using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The browse filter bar (see the XAML). Its own interaction logic is keyboard:
/// Enter applies from anywhere in the bar, Escape hands focus back to the grid
/// (<see cref="ReturnFocusRequested"/>), and <see cref="FocusFilter"/> puts the
/// caret in a row's value input when the bar is opened by a gesture.
/// </summary>
public partial class BrowseFilterBar : UserControl
{
    public BrowseFilterBar()
    {
        InitializeComponent();
        // Tunneled: a ComboBox or date picker would otherwise take Enter for
        // itself. An open dropdown still gets it — that Enter picks an item.
        AddHandler(KeyDownEvent, OnBarKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Raised on Escape so the host can put focus back in the results grid.</summary>
    public event Action? ReturnFocusRequested;

    private TableBrowseViewModel? Model => DataContext as TableBrowseViewModel;

    /// <summary>
    /// Focuses <paramref name="filter"/>'s value input, or its operator when it
    /// takes no value. Posted: a row added a moment ago has no container yet.
    /// </summary>
    public void FocusFilter(BrowseFilterViewModel? filter) =>
        Dispatcher.UIThread.Post(() =>
        {
            var editors = this.GetVisualDescendants().OfType<ColumnValueEditorView>()
                .Where(v => ReferenceEquals(v.DataContext, filter?.Value))
                .ToList();
            if (filter is { ShowsValue: true } && editors.Count > 0)
            {
                editors[0].FocusInput();
                return;
            }

            this.GetVisualDescendants().OfType<ComboBox>().LastOrDefault()?.Focus();
        }, DispatcherPriority.Loaded);

    private void OnBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || e.Source is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            model.ApplyFiltersCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            ReturnFocusRequested?.Invoke();
            e.Handled = true;
        }
    }
}
