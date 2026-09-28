using PgNimbus.Core.Commands;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// One piece of a shortcut row: either a key cap ("Ctrl", "Enter") or the
/// quiet connective text between alternatives ("/", "double-click").
/// </summary>
public sealed record ShortcutToken(string Text, bool IsKey);

/// <summary>One line of the cheat sheet: what it does, and the keys that do it.</summary>
public sealed record ShortcutRow(string Action, IReadOnlyList<ShortcutToken> Tokens);

/// <summary>A titled group of rows — "Query", "SQL editor", …</summary>
public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// Projects <see cref="CommandCatalog"/> into the F1 cheat sheet. The window
/// used to hand-author every row in XAML, which meant a new shortcut had to be
/// remembered in four places; now it's rendered from the same list the key
/// bindings, the palette and the published docs come from.
/// </summary>
public sealed class ShortcutsViewModel
{
    public IReadOnlyList<ShortcutSection> Sections { get; } = Build();

    private static IReadOnlyList<ShortcutSection> Build()
    {
        var scheme = Hotkeys.Scheme;

        return CommandCatalog.CheatSheetSections()
            .Select(section => new ShortcutSection(
                CommandCatalog.CategoryTitle(section.Category).ToUpperInvariant(),
                section.Items.Select(item => new ShortcutRow(item.DisplayName, Tokenize(item, scheme))).ToList()))
            .ToList();
    }

    /// <summary>
    /// A row's key caps: one chip per key, each chord's chips separated by a
    /// quiet "/". The chips carry the scheme's own spelling — "Ctrl" "Enter"
    /// on the Ctrl scheme, "⌘" "↩" on the Cmd scheme — and the scheme's
    /// primary chord comes first (⇧⌘] for the next tab on a Mac).
    /// </summary>
    public static IReadOnlyList<ShortcutToken> Tokenize(CommandDescriptor descriptor, ChordScheme scheme)
    {
        // The one note that is a range of chords rather than prose: draw it as keys
        // like every row around it, not as grey text (0.14.0 release pass).
        if (descriptor.Id == CommandId.GoToTabByNumber)
        {
            return [new ShortcutToken(scheme == ChordScheme.Cmd ? "⌘" : "Ctrl", IsKey: true), new ShortcutToken("1…9", IsKey: true)];
        }

        var tokens = new List<ShortcutToken>(6);

        foreach (var chord in descriptor.ChordsFor(scheme))
        {
            Separate();
            tokens.AddRange(chord.Caps(scheme).Select(cap => new ShortcutToken(cap, IsKey: true)));
        }

        if (descriptor.GestureNoteFor(scheme) is { Length: > 0 } note)
        {
            Separate();
            tokens.Add(new ShortcutToken(note, IsKey: false));
        }

        return tokens;

        void Separate()
        {
            if (tokens.Count > 0)
            {
                tokens.Add(new ShortcutToken("/", IsKey: false));
            }
        }
    }
}
