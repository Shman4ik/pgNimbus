namespace PgNimbus.Core.Commands;

/// <summary>
/// The keys a shortcut can use. Deliberately a Core-local enum rather than
/// Avalonia's <c>Key</c>: the catalog has to stay UI-free (hard rule 1) and
/// unit-testable, so the App owns the one mapping from these to real keys.
/// </summary>
public enum CommandKey
{
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Enter,
    Escape,
    Space,
    Tab,
    Backspace,
    Delete,
    PageUp,
    PageDown,
    Home,
    End,
    Up,
    Down,
    Left,
    Right,
    Comma,
    Slash,
    Plus,
    Minus,
}

/// <summary>
/// Modifiers on a <see cref="Chord"/>. <see cref="Command"/> is the abstract
/// primary modifier that renders as Ctrl or Cmd depending on the resolved
/// scheme; <see cref="Control"/> is a literal Ctrl that stays Ctrl even under
/// the Cmd scheme (completion's Ctrl+Space — Cmd+Space is Spotlight).
/// </summary>
[Flags]
public enum ChordModifiers
{
    None = 0,
    Command = 1,
    Shift = 2,
    Alt = 4,
    Control = 8,
}

/// <summary>
/// One key combination, stored abstractly so the same descriptor renders as
/// Ctrl+Enter or Cmd+Enter without the catalog knowing which platform it's on.
/// </summary>
public readonly record struct Chord(CommandKey Key, ChordModifiers Modifiers = ChordModifiers.None)
{
    /// <summary>
    /// The individual key caps, in display order (Ctrl, Alt, Shift, key) — what
    /// the F1 cheat sheet renders as separate chips.
    /// </summary>
    /// <param name="commandLabel">"Ctrl" or "Cmd" — how <see cref="ChordModifiers.Command"/> is spelled.</param>
    public IReadOnlyList<string> Caps(string commandLabel)
    {
        var caps = new List<string>(4);
        if (Modifiers.HasFlag(ChordModifiers.Command))
        {
            caps.Add(commandLabel);
        }

        if (Modifiers.HasFlag(ChordModifiers.Control))
        {
            caps.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ChordModifiers.Alt))
        {
            caps.Add("Alt");
        }

        if (Modifiers.HasFlag(ChordModifiers.Shift))
        {
            caps.Add("Shift");
        }

        caps.Add(KeyLabel(Key));
        return caps;
    }

    /// <summary>"Ctrl+Shift+F" — the single-string form used by the palette and the docs.</summary>
    public string Label(string commandLabel) => string.Join("+", Caps(commandLabel));

    /// <summary>The display name of a key: "8" for D8, "PgDn" for PageDown, "," for Comma.</summary>
    public static string KeyLabel(CommandKey key) => key switch
    {
        >= CommandKey.D0 and <= CommandKey.D9 => ((int)(key - CommandKey.D0)).ToString(),
        CommandKey.Backspace => "Backspace",
        CommandKey.PageUp => "PgUp",
        CommandKey.PageDown => "PgDn",
        CommandKey.Up => "↑",
        CommandKey.Down => "↓",
        CommandKey.Left => "←",
        CommandKey.Right => "→",
        CommandKey.Comma => ",",
        CommandKey.Slash => "/",
        CommandKey.Plus => "+",
        CommandKey.Minus => "−",
        CommandKey.Escape => "Esc",
        _ => key.ToString(),
    };
}
