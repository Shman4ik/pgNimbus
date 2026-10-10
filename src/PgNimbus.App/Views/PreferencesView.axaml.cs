using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The user-preferences page (theme, editor behaviour, hotkey scheme), hosted in the
/// shell's preferences <c>OverlayPanel</c>. Every control applies its change
/// immediately, so there is no OK/Cancel and Esc simply dismisses it (the overlay
/// owns that).
/// </summary>
public partial class PreferencesView : UserControl
{
    /// <summary>
    /// The page's height whichever tab is showing. The overlay's card sizes to its
    /// content and is centred, so a page sized by its tab would grow and shrink on
    /// every switch and move the tab strip under the pointer. A window too short for
    /// it gets what there is, and each tab scrolls.
    /// </summary>
    public const double PageHeight = 520;

    public PreferencesView() => InitializeComponent();

    private async void OnChooseToolsFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PreferencesViewModel model
            && TopLevel.GetTopLevel(this) is { } owner
            && await BackupWindow.ChooseToolsFolderAsync(owner) is { } folder)
        {
            await model.PgTools.UseFolderAsync(folder);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = Math.Min(PageHeight, availableSize.Height);
        var desired = base.MeasureOverride(availableSize.WithHeight(height));
        return desired.WithHeight(height);
    }
}
