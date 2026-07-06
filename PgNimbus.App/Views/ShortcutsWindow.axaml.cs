using Avalonia.Controls;
using Avalonia.Input;

namespace PgNimbus.App.Views;

/// <summary>
/// A static keyboard-shortcut cheat sheet. Opened from the main window via
/// F1 or the "?" title-bar button; Esc (or F1 again) closes it.
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F1)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
