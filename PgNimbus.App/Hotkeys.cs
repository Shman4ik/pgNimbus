using Avalonia.Input;

namespace PgNimbus.App;

/// <summary>
/// The app's primary command modifier: Cmd (Meta) on macOS, Ctrl elsewhere,
/// overridable via the persisted hotkey-scheme preference ("auto" / "windows"
/// / "mac"). Key bindings, palette shortcut labels, and the cheat sheet all
/// resolve through here so they can't drift apart — never hardcode a Ctrl
/// gesture directly.
/// <para>
/// The resolution itself lives in <see cref="Nimbus.Ui.Hotkeys"/>, shared with
/// kubeNimbus; this type is the pgNimbus-facing name for it (<c>Command</c> rather
/// than <c>Primary</c>) so the call sites throughout the app stay as they were.
/// </para>
/// </summary>
public static class Hotkeys
{
    public static KeyModifiers Command => Nimbus.Ui.Hotkeys.Primary;

    /// <summary>Raised when the scheme changes, so open windows rebuild their bindings.</summary>
    public static event Action? Changed
    {
        add => Nimbus.Ui.Hotkeys.Changed += value;
        remove => Nimbus.Ui.Hotkeys.Changed -= value;
    }

    /// <summary>"Ctrl" or "Cmd" — the display name of <see cref="Command"/>.</summary>
    public static string CommandLabel => Nimbus.Ui.Hotkeys.PrimaryLabel;

    /// <summary>Re-resolves the modifier from the persisted scheme; notifies on an actual change.</summary>
    public static void Initialize(string scheme) => Nimbus.Ui.Hotkeys.Initialize(scheme);

    /// <summary>"Ctrl+Enter" / "Cmd+Enter" — display label for a chord on the command modifier.</summary>
    public static string Label(string key) => Nimbus.Ui.Hotkeys.Label(key);
}
