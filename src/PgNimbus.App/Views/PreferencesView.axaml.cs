using Avalonia.Controls;

namespace PgNimbus.App.Views;

/// <summary>
/// The user-preferences page (theme, editor behaviour, hotkey scheme), hosted in the
/// shell's preferences <c>OverlayPanel</c>. Every control applies its change
/// immediately, so there is no OK/Cancel and Esc simply dismisses it (the overlay
/// owns that).
/// </summary>
public partial class PreferencesView : UserControl
{
    public PreferencesView() => InitializeComponent();
}
