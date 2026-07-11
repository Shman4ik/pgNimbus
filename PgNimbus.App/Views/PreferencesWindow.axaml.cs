using Avalonia.Controls;
using Avalonia.Input;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Views;

/// <summary>
/// The user-preferences page (theme, editor behavior, hotkey scheme). Opened
/// from the command palette; every control applies its change immediately, so
/// there's no OK/Cancel — Esc just closes it.
/// </summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
        Closed += (_, _) => (DataContext as PreferencesViewModel)?.Detach();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
