using Avalonia.Controls;
using Avalonia.Input;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;

namespace PgNimbus.App.Views;

/// <summary>
/// The keyboard-shortcut cheat sheet. Opened from the main window via F1 or the
/// "?" title-bar button; Esc (or F1 again) closes it. Every row is projected
/// from <see cref="CommandCatalog"/> by <see cref="ShortcutsViewModel"/> — the
/// window itself authors nothing, so it can't fall behind the real bindings.
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        // Built here rather than bound in XAML: the key caps are rendered with
        // the Ctrl/Cmd label resolved at open time, so reopening the window
        // after a scheme change shows the new gestures.
        DataContext = new ShortcutsViewModel();
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
