using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The browse filter chips (see the XAML). Owns the one flyout the conditions
/// are written in: a chip opens it on a copy of itself, "+ Filter" (and
/// Ctrl/Cmd+F in the grid, and the palette, via <see cref="OpenNewFilter"/>)
/// on a new condition. Apply closes it; closing it any other way drops the
/// draft, so an abandoned condition never lingers half-applied.
/// </summary>
public partial class BrowseFilterBar : UserControl
{
    private readonly FilterEditorView _editor = new();
    private readonly Flyout _flyout;
    private TableBrowseViewModel? _model;

    public BrowseFilterBar()
    {
        InitializeComponent();
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard,
            Content = _editor,
        };
        _flyout.Opened += (_, _) => _editor.FocusValue();
        _flyout.Closed += (_, _) =>
        {
            if (_model?.Draft is not null)
            {
                _model.CancelDraft();
            }
        };
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Starts a new condition on <paramref name="column"/> and opens its editor.
    /// Posted: the strip may only just have become visible to anchor it.
    /// </summary>
    public void OpenNewFilter(string? column = null)
    {
        if (_model?.BeginNewFilter(column) is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ShowEditor(AddButton), DispatcherPriority.Loaded);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // A tab switch: the flyout belongs to the old tab's draft.
        _flyout.Hide();
        if (_model is not null)
        {
            _model.DraftCommitted -= OnDraftCommitted;
        }

        _model = DataContext as TableBrowseViewModel;
        _editor.DataContext = _model;
        if (_model is not null)
        {
            _model.DraftCommitted += OnDraftCommitted;
        }
    }

    private void OnDraftCommitted() => _flyout.Hide();

    private void OnAddClick(object? sender, RoutedEventArgs e) => OpenNewFilter();

    private void OnChipClick(object? sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not Button { DataContext: BrowseFilterViewModel chip } button)
        {
            return;
        }

        _model.BeginEditFilter(chip);
        ShowEditor(button.Parent as Control ?? button);
    }

    private void ShowEditor(Control anchor)
    {
        if (anchor.IsEffectivelyVisible)
        {
            _flyout.ShowAt(anchor);
        }
    }
}
