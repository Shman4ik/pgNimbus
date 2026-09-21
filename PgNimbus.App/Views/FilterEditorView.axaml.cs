using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The editor for one filter condition, shown in the chip strip's flyout. Enter
/// applies from anywhere in it (tunneled, so a ComboBox can't take the key —
/// an open dropdown still does, that Enter picks an item).
/// </summary>
public partial class FilterEditorView : UserControl
{
    public FilterEditorView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the value input, or on the comparison when the condition takes no value.</summary>
    public void FocusValue() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (this.GetVisualDescendants().OfType<ColumnValueEditorView>().FirstOrDefault(v => v.IsEffectivelyVisible) is { } value)
            {
                value.FocusInput();
            }
            else
            {
                OperatorInput.Focus();
            }
        }, DispatcherPriority.Loaded);

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None
            && e.Source is not ComboBox { IsDropDownOpen: true }
            && DataContext is TableBrowseViewModel model)
        {
            model.CommitDraftCommand.Execute(null);
            e.Handled = true;
        }
    }
}
