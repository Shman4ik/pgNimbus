using Avalonia.Controls;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;

namespace PgNimbus.App.Views;

/// <summary>
/// The keyboard-shortcut cheat sheet's body, hosted in the shell's shortcuts
/// <c>OverlayPanel</c> (F1, the ? button, or the ☰ menu). Every row is projected
/// from <see cref="CommandCatalog"/> by <see cref="ShortcutsViewModel"/> — this view
/// authors nothing, so it can't fall behind the real bindings.
/// </summary>
public partial class ShortcutsView : UserControl
{
    public ShortcutsView() => InitializeComponent();
}
