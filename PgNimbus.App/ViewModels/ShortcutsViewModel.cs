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
        var label = Hotkeys.CommandLabel;

        return CommandCatalog.CheatSheetSections()
            .Select(section => new ShortcutSection(
                CommandCatalog.CategoryTitle(section.Category).ToUpperInvariant(),
                section.Items.Select(item => new ShortcutRow(item.DisplayName, Tokenize(item, label))).ToList()))
            .ToList();
    }

    private static IReadOnlyList<ShortcutToken> Tokenize(CommandDescriptor descriptor, string commandLabel)
    {
        var tokens = new List<ShortcutToken>(6);

        if (descriptor.Chord is { } chord)
        {
            tokens.AddRange(chord.Caps(commandLabel).Select(cap => new ShortcutToken(cap, IsKey: true)));
        }

        if (descriptor.AltChord is { } alt)
        {
            Separate();
            tokens.AddRange(alt.Caps(commandLabel).Select(cap => new ShortcutToken(cap, IsKey: true)));
        }

        if (descriptor.GestureNoteFor(commandLabel) is { Length: > 0 } note)
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
