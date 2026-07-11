using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

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
        ThemedWindowChrome.Attach(this);

        // The key caps are authored as "Ctrl"; when the resolved command
        // modifier is Cmd (macOS, or the explicit mac scheme), relabel every
        // cap marked cmdKey. Ctrl+Space (completion) stays literal Ctrl.
        if (Hotkeys.CommandLabel != "Ctrl")
        {
            foreach (var text in this.GetLogicalDescendants().OfType<TextBlock>())
            {
                if (text.Classes.Contains("cmdKey"))
                {
                    text.Text = Hotkeys.CommandLabel;
                }
            }
        }
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
