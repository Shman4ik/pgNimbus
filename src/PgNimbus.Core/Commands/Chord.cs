using System.Globalization;

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
    Period,
    OpenBracket,
    CloseBracket,
}

/// <summary>
/// Modifiers on a <see cref="Chord"/>. <see cref="Command"/> is the abstract
/// primary modifier that resolves to Ctrl or ⌘ depending on the scheme;
/// <see cref="Control"/> is a literal Ctrl that stays Ctrl even under the Cmd
/// scheme (completion's Ctrl+Space — ⌘Space is Spotlight).
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
/// The two keyboard conventions the app speaks. Picked by the hotkey-scheme
/// preference (the platform by default), and it decides two things at once:
/// which physical key <see cref="ChordModifiers.Command"/> is, and how a chord
/// is <em>spelled</em> — "Ctrl+Shift+F" on <see cref="Ctrl"/>, "⇧⌘F" on
/// <see cref="Cmd"/>, which is how every Mac app writes one.
/// </summary>
public enum ChordScheme
{
    /// <summary>Windows / Linux: the command modifier is Ctrl; chords are words joined by "+".</summary>
    Ctrl,

    /// <summary>macOS: the command modifier is ⌘; chords are Apple's glyphs in ⌃⌥⇧⌘ order.</summary>
    Cmd,
}

/// <summary>
/// Physical modifier keys — what a chord resolves to once the scheme has
/// decided what <see cref="ChordModifiers.Command"/> means. Two chords that
/// resolve to the same keys are the same gesture, whatever they were declared
/// as (Command and Control are both Ctrl on the Ctrl scheme).
/// </summary>
[Flags]
public enum PhysicalModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

/// <summary>One chord with its modifiers resolved to physical keys.</summary>
public readonly record struct PhysicalChord(CommandKey Key, PhysicalModifiers Modifiers);

/// <summary>
/// One key combination, stored abstractly so the same descriptor renders as
/// Ctrl+Enter or ⌘↩ without the catalog knowing which platform it's on.
/// </summary>
public readonly record struct Chord(CommandKey Key, ChordModifiers Modifiers = ChordModifiers.None)
{
    /// <summary>The physical keys this chord is pressed with on <paramref name="scheme"/>.</summary>
    public PhysicalChord Resolve(ChordScheme scheme)
    {
        var result = PhysicalModifiers.None;
        if (Modifiers.HasFlag(ChordModifiers.Command))
        {
            result |= scheme == ChordScheme.Cmd ? PhysicalModifiers.Meta : PhysicalModifiers.Control;
        }

        if (Modifiers.HasFlag(ChordModifiers.Control))
        {
            result |= PhysicalModifiers.Control;
        }

        if (Modifiers.HasFlag(ChordModifiers.Alt))
        {
            result |= PhysicalModifiers.Alt;
        }

        if (Modifiers.HasFlag(ChordModifiers.Shift))
        {
            result |= PhysicalModifiers.Shift;
        }

        return new PhysicalChord(Key, result);
    }

    /// <summary>
    /// The individual key caps, in display order — what the F1 cheat sheet
    /// renders as separate chips. Ctrl scheme: Ctrl, Alt, Shift, then the key,
    /// as words. Cmd scheme: Apple's glyphs in Apple's order, ⌃ ⌥ ⇧ ⌘, then
    /// the key; "Alt" never appears there, it is ⌥ Option.
    /// </summary>
    public IReadOnlyList<string> Caps(ChordScheme scheme)
    {
        var physical = Resolve(scheme).Modifiers;
        var caps = new List<string>(4);

        if (scheme == ChordScheme.Cmd)
        {
            // ⌘? is how a Mac writes Help (⇧⌘/): the shifted character rather
            // than ⇧ plus the unshifted one. Only "?" — digits keep their ⇧
            // (⇧⌘8), since what Shift+8 types depends on the layout.
            var shiftedSlash = Key == CommandKey.Slash && physical.HasFlag(PhysicalModifiers.Shift);

            if (physical.HasFlag(PhysicalModifiers.Control))
            {
                caps.Add("⌃");
            }

            if (physical.HasFlag(PhysicalModifiers.Alt))
            {
                caps.Add("⌥");
            }

            if (physical.HasFlag(PhysicalModifiers.Shift) && !shiftedSlash)
            {
                caps.Add("⇧");
            }

            if (physical.HasFlag(PhysicalModifiers.Meta))
            {
                caps.Add("⌘");
            }

            caps.Add(shiftedSlash ? "?" : KeyLabel(Key, scheme));
            return caps;
        }

        // The Ctrl scheme never resolves anything to Meta, so there is no
        // fourth word to spell.
        if (physical.HasFlag(PhysicalModifiers.Control))
        {
            caps.Add("Ctrl");
        }

        if (physical.HasFlag(PhysicalModifiers.Alt))
        {
            caps.Add("Alt");
        }

        if (physical.HasFlag(PhysicalModifiers.Shift))
        {
            caps.Add("Shift");
        }

        caps.Add(KeyLabel(Key, scheme));
        return caps;
    }

    /// <summary>
    /// The single-string form used by the palette, tooltips and the docs:
    /// "Ctrl+Shift+F" on the Ctrl scheme, "⇧⌘F" on the Cmd scheme (glyphs run
    /// together, the way a Mac menu prints them).
    /// </summary>
    public string Label(ChordScheme scheme) =>
        string.Join(scheme == ChordScheme.Cmd ? string.Empty : "+", Caps(scheme));

    /// <summary>
    /// The display name of a key: "8" for D8, "PgDn" or "⇟" for PageDown,
    /// "Enter" or "↩" for Enter. Letters, digits, F-keys, arrows, punctuation
    /// and Space are spelled the same on both schemes.
    /// </summary>
    public static string KeyLabel(CommandKey key, ChordScheme scheme = ChordScheme.Ctrl)
    {
        if (scheme == ChordScheme.Cmd)
        {
            switch (key)
            {
                case CommandKey.Enter: return "↩";
                case CommandKey.Escape: return "⎋";
                case CommandKey.Tab: return "⇥";
                case CommandKey.Backspace: return "⌫";
                case CommandKey.Delete: return "⌦";
                case CommandKey.PageUp: return "⇞";
                case CommandKey.PageDown: return "⇟";
                case CommandKey.Home: return "↖";
                case CommandKey.End: return "↘";
            }
        }

        return key switch
        {
            >= CommandKey.D0 and <= CommandKey.D9 =>
                ((int)(key - CommandKey.D0)).ToString(CultureInfo.InvariantCulture),
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
            CommandKey.Period => ".",
            CommandKey.OpenBracket => "[",
            CommandKey.CloseBracket => "]",
            CommandKey.Escape => "Esc",
            _ => key.ToString(),
        };
    }

    /// <summary>
    /// Reads a chord written with the catalog's own names — "Escape",
    /// "Shift+Enter", "Cmd+Shift+F" — so a tooltip naming a key that isn't a
    /// catalog command (a search box's Enter, a close button's Esc) can still
    /// be spelled per scheme instead of hardcoding one spelling. "Cmd" is the
    /// abstract command modifier, "Ctrl" a literal Control.
    /// </summary>
    public static bool TryParse(string? text, out Chord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = ChordModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CMD": modifiers |= ChordModifiers.Command; break;
                case "CTRL": modifiers |= ChordModifiers.Control; break;
                case "SHIFT": modifiers |= ChordModifiers.Shift; break;
                case "ALT": modifiers |= ChordModifiers.Alt; break;
                default: return false;
            }
        }

        // Enum.TryParse also accepts numbers ("3" → the fourth key), which
        // would read as a typo'd name silently turning into some other key.
        var name = parts[^1];
        if (name.Length == 0 || char.IsDigit(name[0]) || name[0] == '-'
            || !Enum.TryParse<CommandKey>(name, ignoreCase: true, out var key))
        {
            return false;
        }

        chord = new Chord(key, modifiers);
        return true;
    }
}
