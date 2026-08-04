using Avalonia.Input;

namespace Nimbus.Ui;

/// <summary>
/// The primary command modifier — Cmd (Meta) on macOS, Ctrl elsewhere — and the
/// labelling that goes with it. Key bindings, command-palette shortcut labels and
/// the keyboard cheat sheet all resolve through here, so they cannot drift apart.
/// <para>
/// <b>Never hardcode a Ctrl gesture in a view.</b> That includes gestures built in a
/// loop: Ctrl/Cmd+1…9 for tab jumps are registered from <see cref="Primary"/> in
/// code-behind, not as nine XAML <c>KeyBinding</c>s.
/// </para>
/// </summary>
public static class Hotkeys
{
    /// <summary>
    /// Cmd on macOS, Ctrl everywhere else — unless <see cref="Initialize"/> has been
    /// called with an explicit scheme.
    /// <para>
    /// Read this at the moment a gesture is built, not into a <c>static readonly</c>
    /// field: the scheme is user-overridable, so a gesture captured at type-init
    /// outlives the setting that produced it. Rebuild bindings from
    /// <see cref="Changed"/>.
    /// </para>
    /// </summary>
    public static KeyModifiers Primary { get; private set; } = Resolve("auto");

    /// <summary>Raised when the scheme changes, so open windows can rebuild their bindings.</summary>
    public static event Action? Changed;

    /// <summary>"Ctrl" or "Cmd" — the display name of <see cref="Primary"/>.</summary>
    public static string PrimaryLabel => Primary.HasFlag(KeyModifiers.Meta) ? "Cmd" : "Ctrl";

    /// <summary>
    /// Re-resolves the modifier from a persisted scheme ("auto" / "windows" / "mac");
    /// raises <see cref="Changed"/> only on an actual change. The override exists
    /// because the platform default is a good guess, not a fact: people who moved from
    /// a Mac keep muscle memory for Cmd, and people running macOS with an external PC
    /// keyboard want Ctrl.
    /// </summary>
    public static void Initialize(string scheme)
    {
        var resolved = Resolve(scheme);
        if (resolved == Primary)
        {
            return;
        }

        Primary = resolved;
        Changed?.Invoke();
    }

    /// <summary>"Ctrl+Enter" / "Cmd+Enter" — a label for a chord on the primary modifier.</summary>
    public static string Label(string key) => $"{PrimaryLabel}+{key}";

    /// <summary>
    /// Human-readable label for a gesture that already exists, for palette rows and the
    /// cheat sheet. Reads the modifier off the gesture rather than assuming
    /// <see cref="Primary"/>, so a deliberately-literal Ctrl gesture (a terminal's
    /// interrupt is Control on macOS too) describes itself honestly.
    /// </summary>
    public static string Describe(KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);

        var mod = gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "Cmd"
            : gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? "Ctrl"
            : "";

        return string.IsNullOrEmpty(mod) ? gesture.Key.ToString() : $"{mod}+{gesture.Key}";
    }

    /// <summary>One row of a keyboard cheat sheet: what it does, and what to press.</summary>
    public sealed record ShortcutEntry(string Action, string Keys);

    private static KeyModifiers Resolve(string scheme) => scheme switch
    {
        "windows" => KeyModifiers.Control,
        "mac" => KeyModifiers.Meta,
        _ => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control,
    };
}
