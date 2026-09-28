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
    [Arguments(ChordScheme.Ctrl)]
    [Arguments(ChordScheme.Cmd)]
    public async Task NoTwoCommandsInTheSameScopeShareAChord(ChordScheme scheme)
    {
        // Compared as physical keys, per scheme: a literal-Ctrl chord and a
        // command-modifier chord are two gestures on the Cmd scheme and one on
        // the Ctrl scheme, and only resolving them shows that.
        var clashes = CommandCatalog.All
            .SelectMany(d => d.ChordsFor(scheme).Distinct()
                .Select(c => (d.Scope, Keys: c.Resolve(scheme), Label: c.Label(scheme), d.Id)))
            .GroupBy(x => (x.Scope, x.Keys))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Scope}/{g.First().Label}: {string.Join(", ", g.Select(x => x.Id))}")
            .ToList();

        await Assert.That(clashes).IsEmpty();
    }

    [Test]
    [Arguments(ChordScheme.Ctrl)]
    [Arguments(ChordScheme.Cmd)]
    public async Task PanelChordsCarryingCtrlOrCmdDontShadowGlobalOnes(ChordScheme scheme)
    {
        // A panel-scoped gesture that includes Ctrl or ⌘ bubbles up to the
        // window's KeyBindings, so it must not collide with a global one even
        // though the scopes differ.
        var global = CommandCatalog.All
            .Where(d => d.Scope == CommandScope.Global)
            .SelectMany(d => d.ChordsFor(scheme).Select(c => (Keys: c.Resolve(scheme), d.Id)))
            .ToList();

        var clashes = CommandCatalog.All
            .Where(d => d.Scope != CommandScope.Global)
            .SelectMany(d => d.ChordsFor(scheme).Select(c => (Chord: c, Keys: c.Resolve(scheme), d.Id)))
            .Where(x => (x.Keys.Modifiers & (PhysicalModifiers.Control | PhysicalModifiers.Meta)) != 0)
            .SelectMany(x => global
                .Where(g => g.Keys == x.Keys)
                .Select(g => $"{x.Chord.Label(scheme)}: {x.Id} vs {g.Id}"))
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
            .Where(d => d.ShortcutLabel(ChordScheme.Ctrl) is null || d.ShortcutLabel(ChordScheme.Cmd) is null)
            .Select(d => d.Id.ToString())
            .ToList();

        await Assert.That(empty).IsEmpty();
    }

    [Test]
    public async Task ASchemeOnlyChordAppearsOnlyOnItsScheme()
    {
        var bracket = new Chord(CommandKey.CloseBracket, ChordModifiers.Command | ChordModifiers.Shift);
        var next = CommandCatalog.Get(CommandId.NextTab);

        await Assert.That(next.ChordsFor(ChordScheme.Ctrl)).DoesNotContain(bracket);
        await Assert.That(next.ChordsFor(ChordScheme.Cmd)).Contains(bracket);
    }

    [Test]
    public async Task ThePlatformConventionIsThePrimaryGestureOnItsScheme()
    {
        // The sheet used to advertise ⌘PgDn for the next tab — a key a Mac
        // keyboard doesn't have — and F1, which needs Fn there.
        await Assert.That(Primary(CommandId.NextTab, ChordScheme.Cmd)).IsEqualTo("⇧⌘]");
        await Assert.That(Primary(CommandId.PreviousTab, ChordScheme.Cmd)).IsEqualTo("⇧⌘[");
        await Assert.That(Primary(CommandId.ShortcutsWindow, ChordScheme.Cmd)).IsEqualTo("⌘?");
        await Assert.That(Primary(CommandId.Cancel, ChordScheme.Cmd)).IsEqualTo("⎋");

        // …and the Ctrl scheme keeps the gestures it always had.
        await Assert.That(Primary(CommandId.NextTab, ChordScheme.Ctrl)).IsEqualTo("Ctrl+PgDn");
        await Assert.That(Primary(CommandId.PreviousTab, ChordScheme.Ctrl)).IsEqualTo("Ctrl+PgUp");
        await Assert.That(Primary(CommandId.ShortcutsWindow, ChordScheme.Ctrl)).IsEqualTo("F1");
        await Assert.That(Primary(CommandId.Run, ChordScheme.Ctrl)).IsEqualTo("Ctrl+Enter");

        static string? Primary(CommandId id, ChordScheme scheme) =>
            CommandCatalog.ChordFor(id, scheme)?.Label(scheme);
    }

    [Test]
    public async Task EveryCrossPlatformChordStaysASynonym()
    {
        // Promoting ⇧⌘] mustn't retire ⌘PgDn for someone with a full keyboard.
        await Assert.That(CommandCatalog.Get(CommandId.NextTab).SynonymsFor(ChordScheme.Cmd))
            .Contains(new Chord(CommandKey.PageDown, ChordModifiers.Command));
        await Assert.That(CommandCatalog.Get(CommandId.NextTab).SynonymsFor(ChordScheme.Ctrl))
            .Contains(new Chord(CommandKey.Tab, ChordModifiers.Control));
        await Assert.That(CommandCatalog.Get(CommandId.ShortcutsWindow).SynonymsFor(ChordScheme.Cmd))
            .Contains(new Chord(CommandKey.F1));
        await Assert.That(CommandCatalog.Get(CommandId.Cancel).SynonymsFor(ChordScheme.Cmd))
            .Contains(new Chord(CommandKey.Period, ChordModifiers.Command));
        await Assert.That(CommandCatalog.Get(CommandId.Cancel).SynonymsFor(ChordScheme.Ctrl))
            .DoesNotContain(new Chord(CommandKey.Period, ChordModifiers.Command));
    }

    [Test]
    public async Task TheCmdSchemeSpellsNoModifierOrSpecialKeyAsAWord()
    {
        // "Cmd+Enter / F5", "Cmd+Shift+Backspace" and "Alt+Shift+F" were what
        // the palette showed on a Mac. Only Space, F-keys and letters may still
        // be letters there.
        string[] words = ["Ctrl", "Cmd", "Alt", "Shift", "Enter", "Backspace", "Delete", "Esc", "Tab", "PgUp", "PgDn", "Home", "End", "Meta"];
        var wordy = CommandCatalog.All
            .Select(d => (d.Id, Label: d.ShortcutLabel(ChordScheme.Cmd) ?? string.Empty))
            .Where(x => words.Any(w => x.Label.Contains(w, StringComparison.Ordinal)))
            .Select(x => $"{x.Id}: {x.Label}")
            .ToList();

        await Assert.That(wordy).IsEmpty();
    }

    [Test]
    public async Task ChordLabelsFollowTheResolvedScheme()
    {
        var chord = new Chord(CommandKey.F, ChordModifiers.Command | ChordModifiers.Shift);

        await Assert.That(chord.Label(ChordScheme.Ctrl)).IsEqualTo("Ctrl+Shift+F");
        await Assert.That(chord.Label(ChordScheme.Cmd)).IsEqualTo("⇧⌘F");
    }

    [Test]
    [Arguments(CommandKey.Enter, ChordModifiers.Command, "Ctrl+Enter", "⌘↩")]
    [Arguments(CommandKey.Backspace, ChordModifiers.Command | ChordModifiers.Shift, "Ctrl+Shift+Backspace", "⇧⌘⌫")]
    [Arguments(CommandKey.F, ChordModifiers.Alt | ChordModifiers.Shift, "Alt+Shift+F", "⌥⇧F")]
    [Arguments(CommandKey.K, ChordModifiers.Command, "Ctrl+K", "⌘K")]
    [Arguments(CommandKey.Escape, ChordModifiers.None, "Esc", "⎋")]
    [Arguments(CommandKey.Tab, ChordModifiers.Control | ChordModifiers.Shift, "Ctrl+Shift+Tab", "⌃⇧⇥")]
    [Arguments(CommandKey.Delete, ChordModifiers.None, "Delete", "⌦")]
    [Arguments(CommandKey.PageDown, ChordModifiers.Command, "Ctrl+PgDn", "⌘⇟")]
    [Arguments(CommandKey.PageUp, ChordModifiers.Command, "Ctrl+PgUp", "⌘⇞")]
    [Arguments(CommandKey.Up, ChordModifiers.Alt, "Alt+↑", "⌥↑")]
    [Arguments(CommandKey.Space, ChordModifiers.Control, "Ctrl+Space", "⌃Space")]
    [Arguments(CommandKey.Slash, ChordModifiers.Command | ChordModifiers.Shift, "Ctrl+Shift+/", "⌘?")]
    [Arguments(CommandKey.Period, ChordModifiers.Command, "Ctrl+.", "⌘.")]
    [Arguments(CommandKey.D8, ChordModifiers.Command | ChordModifiers.Shift, "Ctrl+Shift+8", "⇧⌘8")]
    public async Task EachSchemeSpellsAChordItsOwnWay(CommandKey key, ChordModifiers modifiers, string ctrl, string cmd)
    {
        var chord = new Chord(key, modifiers);

        await Assert.That(chord.Label(ChordScheme.Ctrl)).IsEqualTo(ctrl);
        await Assert.That(chord.Label(ChordScheme.Cmd)).IsEqualTo(cmd);
    }

    [Test]
    public async Task CmdSchemeCapsComeInApplesOrder()
    {
        var all = new Chord(CommandKey.A,
            ChordModifiers.Command | ChordModifiers.Shift | ChordModifiers.Alt | ChordModifiers.Control);

        await Assert.That(string.Join(" ", all.Caps(ChordScheme.Cmd))).IsEqualTo("⌃ ⌥ ⇧ ⌘ A");
        await Assert.That(string.Join(" ", new Chord(CommandKey.Enter, ChordModifiers.Command).Caps(ChordScheme.Ctrl)))
            .IsEqualTo("Ctrl Enter");
    }

    [Test]
    public async Task LiteralControlStaysCtrlUnderTheCmdScheme()
    {
        // Completion's Ctrl+Space must not become ⌘Space (that's Spotlight).
        var chord = new Chord(CommandKey.Space, ChordModifiers.Control);

        await Assert.That(chord.Resolve(ChordScheme.Cmd).Modifiers).IsEqualTo(PhysicalModifiers.Control);
        await Assert.That(chord.Label(ChordScheme.Cmd)).IsEqualTo("⌃Space");
    }

    [Test]
    [Arguments(CommandKey.D8, "8")]
    [Arguments(CommandKey.PageDown, "PgDn")]
    [Arguments(CommandKey.Comma, ",")]
    [Arguments(CommandKey.Slash, "/")]
    [Arguments(CommandKey.Escape, "Esc")]
    [Arguments(CommandKey.Enter, "Enter")]
    [Arguments(CommandKey.F5, "F5")]
    [Arguments(CommandKey.CloseBracket, "]")]
    public async Task KeyLabelsAreHumanReadable(CommandKey key, string expected)
    {
        await Assert.That(Chord.KeyLabel(key)).IsEqualTo(expected);
    }

    [Test]
    public async Task GestureNotesResolveTheModifierPlaceholder()
    {
        var descriptor = CommandCatalog.Get(CommandId.GoToTabByNumber);

        await Assert.That(descriptor.GestureNoteFor(ChordScheme.Cmd)).IsEqualTo("⌘1 … ⌘9");
        await Assert.That(descriptor.ShortcutLabel(ChordScheme.Ctrl)).IsEqualTo("Ctrl+1 … Ctrl+9");
        await Assert.That(CommandCatalog.Get(CommandId.ZoomEditor).GestureNoteFor(ChordScheme.Cmd))
            .IsEqualTo("⌘ + wheel");
        await Assert.That(CommandCatalog.Get(CommandId.ZoomEditor).GestureNoteFor(ChordScheme.Ctrl))
            .IsEqualTo("Ctrl+wheel");
        await Assert.That(CommandCatalog.Get(CommandId.FilterRows).GestureNoteFor(ChordScheme.Cmd))
            .IsEqualTo("⌘F in the results grid while browsing a table");
    }

    /// <summary>
    /// Close's undo sits on the browser gesture (Cmd+Shift+T on a Mac, Ctrl+Shift+T
    /// elsewhere) and on every surface, since the status line names it after a
    /// close and has to name something the user can actually press or find.
    /// </summary>
    [Test]
    public async Task ReopenClosedTabIsTheBrowserGestureEverywhere()
    {
        var descriptor = CommandCatalog.Get(CommandId.ReopenClosedTab);

        await Assert.That(descriptor.Chord).IsEqualTo(new Chord(CommandKey.T, ChordModifiers.Command | ChordModifiers.Shift));
        await Assert.That(descriptor.Category).IsEqualTo(CommandCategory.Tabs);
        await Assert.That(descriptor.In(CommandSurface.WindowBinding)).IsTrue();
        await Assert.That(descriptor.In(CommandSurface.Palette)).IsTrue();
        await Assert.That(descriptor.In(CommandSurface.CheatSheet)).IsTrue();
        await Assert.That(descriptor.ShortcutLabel(ChordScheme.Cmd)).IsEqualTo("⇧⌘T");
        await Assert.That(descriptor.ShortcutLabel(ChordScheme.Ctrl)).IsEqualTo("Ctrl+Shift+T");
    }

    [Test]
    [Arguments("Escape", CommandKey.Escape, ChordModifiers.None)]
    [Arguments("Shift+Enter", CommandKey.Enter, ChordModifiers.Shift)]
    [Arguments("cmd+shift+f", CommandKey.F, ChordModifiers.Command | ChordModifiers.Shift)]
    [Arguments("Ctrl+Space", CommandKey.Space, ChordModifiers.Control)]
    public async Task ChordsParseFromTheirNames(string text, CommandKey key, ChordModifiers modifiers)
    {
        await Assert.That(Chord.TryParse(text, out var chord)).IsTrue();
        await Assert.That(chord).IsEqualTo(new Chord(key, modifiers));
    }

    [Test]
    [Arguments("")]
    [Arguments("Hyper+K")]
    [Arguments("Shift+")]
    [Arguments("3")]
    [Arguments("NoSuchKey")]
    public async Task NonsenseDoesNotParse(string text)
    {
        await Assert.That(Chord.TryParse(text, out _)).IsFalse();
    }
}
