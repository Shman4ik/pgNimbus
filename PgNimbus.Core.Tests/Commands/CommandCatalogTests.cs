using PgNimbus.Core.Commands;

namespace PgNimbus.Core.Tests.Commands;

/// <summary>
/// Guards the one property the catalog exists to provide: that every surface
/// (key bindings, palette, macOS menu, F1 sheet, docs) is describing the same
/// set of commands, with no two gestures fighting over the same keys.
/// </summary>
public class CommandCatalogTests
{
    [Test]
    public async Task EveryIdAppearsExactlyOnce()
    {
        var duplicates = CommandCatalog.All
            .GroupBy(d => d.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.ToString())
            .ToList();

        await Assert.That(duplicates).IsEmpty();
    }

    [Test]
    public async Task EveryCommandIdIsInTheCatalog()
    {
        // A new CommandId with no descriptor would be invisible everywhere.
        var missing = Enum.GetValues<CommandId>()
            .Where(id => CommandCatalog.All.All(d => d.Id != id))
            .Select(id => id.ToString())
            .ToList();

        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task NoTwoCommandsInTheSameScopeShareAChord()
    {
        var clashes = CommandCatalog.All
            .SelectMany(d => Chords(d).Select(c => (d.Scope, Chord: c, d.Id)))
            .GroupBy(x => (x.Scope, x.Chord))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Scope}/{g.Key.Chord.Label("Ctrl")}: {string.Join(", ", g.Select(x => x.Id))}")
            .ToList();

        await Assert.That(clashes).IsEmpty();
    }

    [Test]
    public async Task PanelChordsCarryingTheCommandModifierDontShadowGlobalOnes()
    {
        // A panel-scoped gesture that includes Ctrl/Cmd bubbles up to the
        // window's KeyBindings, so it must not collide with a global one even
        // though the scopes differ.
        var global = CommandCatalog.All
            .Where(d => d.Scope == CommandScope.Global)
            .SelectMany(d => Chords(d).Select(c => (Chord: c, d.Id)))
            .ToList();

        var clashes = CommandCatalog.All
            .Where(d => d.Scope != CommandScope.Global)
            .SelectMany(d => Chords(d).Select(c => (Chord: c, d.Id)))
            .Where(x => x.Chord.Modifiers.HasFlag(ChordModifiers.Command))
            .SelectMany(x => global
                .Where(g => g.Chord == x.Chord)
                .Select(g => $"{x.Chord.Label("Ctrl")}: {x.Id} vs {g.Id}"))
            .ToList();

        await Assert.That(clashes).IsEmpty();
    }

    [Test]
    public async Task PaletteEntriesHaveAGlyph()
    {
        var bland = CommandCatalog.On(CommandSurface.Palette)
            .Where(d => string.IsNullOrWhiteSpace(d.Glyph) || d.Glyph == "•")
            .Select(d => d.Id.ToString())
            .ToList();

        await Assert.That(bland).IsEmpty();
    }

    [Test]
    public async Task WindowBindingsAndPaletteEntriesAreGloballyScoped()
    {
        // A panel-owned gesture can't be projected into the window's bindings;
        // if one claims WindowBinding the projection would silently misfire.
        var misfiled = CommandCatalog.All
            .Where(d => d.In(CommandSurface.WindowBinding) && d.Scope != CommandScope.Global)
            .Select(d => d.Id.ToString())
            .ToList();

        await Assert.That(misfiled).IsEmpty();
    }

    [Test]
    public async Task EveryWindowBindingHasAChord()
    {
        var chordless = CommandCatalog.On(CommandSurface.WindowBinding)
            .Where(d => d.Chord is null)
            .Select(d => d.Id.ToString())
            .ToList();

        await Assert.That(chordless).IsEmpty();
    }

    [Test]
    public async Task EveryCheatSheetRowShowsAGesture()
    {
        // A cheat-sheet row with neither keys nor a note is an empty line.
        var empty = CommandCatalog.On(CommandSurface.CheatSheet)
            .Where(d => d.ShortcutLabel("Ctrl") is null)
            .Select(d => d.Id.ToString())
            .ToList();

        await Assert.That(empty).IsEmpty();
    }

    [Test]
    public async Task ChordLabelsFollowTheResolvedScheme()
    {
        var chord = new Chord(CommandKey.F, ChordModifiers.Command | ChordModifiers.Shift);

        await Assert.That(chord.Label("Ctrl")).IsEqualTo("Ctrl+Shift+F");
        await Assert.That(chord.Label("Cmd")).IsEqualTo("Cmd+Shift+F");
    }

    [Test]
    public async Task LiteralControlStaysCtrlUnderTheCmdScheme()
    {
        // Completion's Ctrl+Space must not become Cmd+Space (that's Spotlight).
        var chord = new Chord(CommandKey.Space, ChordModifiers.Control);

        await Assert.That(chord.Label("Cmd")).IsEqualTo("Ctrl+Space");
    }

    [Test]
    [Arguments(CommandKey.D8, "8")]
    [Arguments(CommandKey.PageDown, "PgDn")]
    [Arguments(CommandKey.Comma, ",")]
    [Arguments(CommandKey.Slash, "/")]
    [Arguments(CommandKey.Escape, "Esc")]
    [Arguments(CommandKey.Enter, "Enter")]
    [Arguments(CommandKey.F5, "F5")]
    public async Task KeyLabelsAreHumanReadable(CommandKey key, string expected)
    {
        await Assert.That(Chord.KeyLabel(key)).IsEqualTo(expected);
    }

    [Test]
    public async Task GestureNotesResolveTheModifierPlaceholder()
    {
        var descriptor = CommandCatalog.Get(CommandId.GoToTabByNumber);

        await Assert.That(descriptor.GestureNoteFor("Cmd")).IsEqualTo("Cmd+1 … Cmd+9");
        await Assert.That(descriptor.ShortcutLabel("Ctrl")).IsEqualTo("Ctrl+1 … Ctrl+9");
    }

    private static IEnumerable<Chord> Chords(CommandDescriptor descriptor)
    {
        if (descriptor.Chord is { } chord)
        {
            yield return chord;
        }

        if (descriptor.AltChord is { } alt)
        {
            yield return alt;
        }
    }
}
