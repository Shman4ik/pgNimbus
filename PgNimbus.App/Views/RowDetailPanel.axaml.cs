using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;

namespace PgNimbus.App.Views;

/// <summary>
/// The row-detail sidebar (see the XAML). Owns its keyboard model: the same
/// chords that commit and cancel a cell edit in the grid
/// (<see cref="CommandId.CommitCellEdit"/>) stage and revert here, and an
/// Escape with nothing to revert hands focus back to the grid.
/// </summary>
public partial class RowDetailPanel : UserControl
{
    public RowDetailPanel()
    {
        InitializeComponent();
        // Tunneled so a ComboBox or date picker in a field can't swallow Enter
        // first; an open dropdown keeps it (that Enter picks an item).
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
    }

    /// <summary>The ✕ was pressed.</summary>
    public event Action? CloseRequested;

    /// <summary>Escape with nothing to revert: the host puts focus back in the grid.</summary>
    public event Action? ReturnFocusRequested;

    private RowDetailViewModel? Model => DataContext as RowDetailViewModel;

    /// <summary>Focuses the first editable field (or the panel itself when none is). Posted past layout.</summary>
    public void FocusFirstField() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (this.GetVisualDescendants().OfType<ColumnValueEditorView>().FirstOrDefault(v => v.IsEffectivelyVisible) is { } editor)
            {
                editor.FocusInput();
            }
            else
            {
                CloseButton.Focus();
            }
        }, DispatcherPriority.Loaded);

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || e.Source is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        if (CommandBindings.Matches(CommandId.CommitCellEdit, e))
        {
            if (model.StageCommand.CanExecute(null))
            {
                model.StageCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (CommandBindings.MatchesAlt(CommandId.CommitCellEdit, e))
        {
            if (model.RevertCommand.CanExecute(null))
            {
                model.RevertCommand.Execute(null);
            }
            else
            {
                ReturnFocusRequested?.Invoke();
            }

            e.Handled = true;
        }
    }
}
