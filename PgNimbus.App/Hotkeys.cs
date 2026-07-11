using Avalonia.Input;

namespace PgNimbus.App;

/// <summary>
/// The app's primary command modifier: Cmd (Meta) on macOS, Ctrl elsewhere,
/// overridable via the persisted hotkey-scheme preference ("auto" / "windows"
/// / "mac"). Key bindings, palette shortcut labels, and the cheat sheet all
/// resolve through here so they can't drift apart — never hardcode a Ctrl
/// gesture directly.
/// </summary>
public static class Hotkeys
{
    public static KeyModifiers Command { get; private set; } = Resolve("auto");

    /// <summary>Raised when the scheme changes, so open windows rebuild their bindings.</summary>
    public static event Action? Changed;

    /// <summary>"Ctrl" or "Cmd" — the display name of <see cref="Command"/>.</summary>
    public static string CommandLabel => Command == KeyModifiers.Meta ? "Cmd" : "Ctrl";

    /// <summary>Re-resolves the modifier from the persisted scheme; notifies on an actual change.</summary>
    public static void Initialize(string scheme)
    {
        var resolved = Resolve(scheme);
        if (resolved == Command)
        {
            return;
        }

        Command = resolved;
        Changed?.Invoke();
    }

    /// <summary>"Ctrl+Enter" / "Cmd+Enter" — display label for a chord on the command modifier.</summary>
    public static string Label(string key) => $"{CommandLabel}+{key}";

    private static KeyModifiers Resolve(string scheme) => scheme switch
    {
        "windows" => KeyModifiers.Control,
        "mac" => KeyModifiers.Meta,
        _ => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control,
    };
}
