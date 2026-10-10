namespace PgNimbus.Core.Commands;

/// <summary>
/// Stable identity for every command and documented gesture in the app. The
/// name is the id used in generated docs, so renaming one is a doc-visible
/// change — treat it like renaming a public API.
/// </summary>
public enum CommandId
{
    // --- Query ---
    Run,
    RunStatementUnderCursor,
    Cancel,
    Explain,
    ExplainAnalyze,
    ImportPlan,
    FormatSql,
    ExpandStar,
    BeginTransaction,
    CommitTransaction,
    RollbackTransaction,
    ToggleSafeMode,

    // --- Tabs & files ---
    NewTab,
    CloseTab,
    CloseOtherTabs,
    CloseTabsToTheRight,
    RenameTab,
    NextTab,
    PreviousTab,
    GoToTabByNumber,
    OpenFile,
    Save,
    SaveAs,
    SaveQuery,
    SaveFile,

    // --- SQL editor ---
    Completion,
    ParameterHints,
    Find,
    FindReplace,
    FindNextPrevious,
    ToggleLineComment,
    DuplicateLine,
    MoveLineUp,
    MoveLineDown,
    DeleteLine,
    UndoRedo,
    ToggleWordWrap,
    ToggleAutoAlias,
    ZoomEditor,
    ResetEditorZoom,
    WordNavigation,

    // --- Results grid ---
    EditCell,
    CommitCellEdit,
    SetCellNull,
    InspectCell,
    SaveInspectedValue,
    CopySelection,
    DeleteRow,
    RowDetails,
    FilterRows,

    // --- Navigation & app ---
    CommandPalette,
    RefreshSchema,
    FocusSwap,
    ToggleSidebar,
    PreviewTable,
    ServerActivity,
    DatabaseOverview,
    SlowQueries,
    NotifyMonitor,
    SecurityManager,
    SwitchConnection,
    NewWindow,
    ToggleTheme,
    Preferences,
    ShortcutsWindow,

    // Appended rather than filed under "Tabs & files" above so the existing
    // members keep their values; the catalog files it with the tab commands.
    ReopenClosedTab,

    // Appended for the same reason; filed under navigation with the windows.
    BackupDatabase,
}

/// <summary>
/// Which keyboard context owns a gesture. Global gestures reach the main
/// window; <see cref="Editor"/> and <see cref="Results"/> ones are handled by
/// their panel's key handler and only have to be unique within that panel
/// (Enter means "commit the cell edit" in the grid and something else
/// elsewhere).
/// </summary>
public enum CommandScope
{
    Global,
    Editor,
    Results,
}

/// <summary>Cheat-sheet / documentation section a command is filed under.</summary>
public enum CommandCategory
{
    Query,
    Tabs,
    Editor,
    Results,
    Navigation,
}

/// <summary>
/// Where a descriptor surfaces. Note there is no flag for the editor- and
/// grid-level gestures (Ctrl+/, Shift+Enter, Space to inspect …): those are
/// handled inside their own panel's key handler, which looks the chord up by
/// id, so no surface projection applies to them — they only need
/// <see cref="CheatSheet"/> so they appear in F1 and the docs.
/// </summary>
[Flags]
public enum CommandSurface
{
    None = 0,

    /// <summary>Gets a <c>KeyBinding</c> on the main window.</summary>
    WindowBinding = 1,

    /// <summary>Listed in the Ctrl+K command palette.</summary>
    Palette = 2,

    /// <summary>Listed in the F1 cheat sheet and the generated docs.</summary>
    CheatSheet = 4,
}

/// <summary>
/// One row of the app's command catalog: what it's called, where it shows up,
/// and which keys invoke it. Everything that used to be duplicated across
/// <c>BuildKeyBindings</c>, the macOS native menu, the palette and the F1
/// window is stated here exactly once.
/// </summary>
public sealed record CommandDescriptor
{
    public required CommandId Id { get; init; }

    /// <summary>The command-palette label — the long, searchable one.</summary>
    public required string Title { get; init; }

    /// <summary>A shorter label for the cheat sheet and docs; falls back to <see cref="Title"/>.</summary>
    public string? CheatTitle { get; init; }

    public required CommandCategory Category { get; init; }

    /// <summary>Which key handler owns this gesture; see <see cref="CommandScope"/>.</summary>
    public CommandScope Scope { get; init; } = CommandScope.Global;

    /// <summary>Single-character icon for the palette row.</summary>
    public string Glyph { get; init; } = "•";

    /// <summary>The primary key combination; null for palette-only actions.</summary>
    public Chord? Chord { get; init; }

    /// <summary>
    /// A second gesture listed after the first: a synonym (F5 for Run, Ctrl+P
    /// for the palette) or the other half of a pair (Escape beside Enter's
    /// commit, Redo beside Undo). Panels tell the two apart with
    /// <c>CommandBindings.MatchesAlt</c>.
    /// </summary>
    public Chord? AltChord { get; init; }

    /// <summary>
    /// Further synonyms of <see cref="Chord"/>, each optionally confined to one
    /// scheme — the place for a platform's own convention that the other
    /// platform doesn't share (⇧⌘] for the next tab, ⌘. to cancel, ⌘? for
    /// help). One marked <see cref="SchemeChord.Primary"/> is listed first on
    /// its scheme and is the gesture tooltips, menus and the search pill show
    /// there; <see cref="Chord"/> then stays accepted as a synonym.
    /// </summary>
    public IReadOnlyList<SchemeChord> MoreChords { get; init; } = [];

    /// <summary>
    /// Free text for gestures that aren't a chord at all ("Double-click",
    /// "context menu") or a range too wide to enumerate ("{cmd}+1 … {cmd}+9").
    /// Rendered as quiet text next to (or instead of) the key caps; "{cmd}+" is
    /// spelled per scheme ("Ctrl+" or "⌘"), and "{chord:Run}" is another
    /// command's primary chord, spelled per scheme ("Ctrl+Enter" or "⌘↩"), for a
    /// panel that answers that command's chord with its own action.
    /// </summary>
    public string? GestureNote { get; init; }

    public CommandSurface Surfaces { get; init; } = CommandSurface.CheatSheet;

    /// <summary>The label to show outside the palette.</summary>
    public string DisplayName => CheatTitle ?? Title;

    public bool In(CommandSurface surface) => Surfaces.HasFlag(surface);

    /// <summary>
    /// Every chord that invokes this entry on <paramref name="scheme"/>, in
    /// display order: a scheme's promoted chords, then <see cref="Chord"/>,
    /// then <see cref="AltChord"/>, then the remaining synonyms.
    /// </summary>
    public IReadOnlyList<Chord> ChordsFor(ChordScheme scheme)
    {
        var chords = new List<Chord>(2 + MoreChords.Count);
        foreach (var more in MoreChords)
        {
            if (more.Primary && more.AppliesTo(scheme))
            {
                chords.Add(more.Chord);
            }
        }

        if (Chord is { } chord)
        {
            chords.Add(chord);
        }

        if (AltChord is { } alt)
        {
            chords.Add(alt);
        }

        foreach (var more in MoreChords)
        {
            if (!more.Primary && more.AppliesTo(scheme))
            {
                chords.Add(more.Chord);
            }
        }

        return chords;
    }

    /// <summary>
    /// The gesture to name when there is room for one — a tooltip, a menu
    /// caption, the search pill. Null for palette-only actions.
    /// </summary>
    public Chord? PrimaryChordFor(ChordScheme scheme)
    {
        var chords = ChordsFor(scheme);
        return chords.Count == 0 ? null : chords[0];
    }

    /// <summary>
    /// The synonyms of the primary action on <paramref name="scheme"/>: every
    /// chord except <see cref="AltChord"/>, which may be a different action
    /// (Escape beside Enter). What <c>CommandBindings.Matches</c> accepts.
    /// </summary>
    public IEnumerable<Chord> SynonymsFor(ChordScheme scheme)
    {
        if (Chord is { } chord)
        {
            yield return chord;
        }

        foreach (var more in MoreChords)
        {
            if (more.AppliesTo(scheme))
            {
                yield return more.Chord;
            }
        }
    }

    /// <summary>
    /// <see cref="GestureNote"/> spelled for <paramref name="scheme"/>:
    /// "Ctrl+1 … Ctrl+9" or "⌘1 … ⌘9". A key name longer than one character
    /// keeps a visible join on the Cmd scheme ("⌘ + wheel"), where running the
    /// glyph into a word would read as one token.
    /// </summary>
    public string? GestureNoteFor(ChordScheme scheme)
    {
        const string Placeholder = "{cmd}+";
        if (GestureNote is not { } note)
        {
            return null;
        }

        note = SpellChordPlaceholders(note, scheme);
        if (scheme == ChordScheme.Ctrl)
        {
            return note.Replace("{cmd}", "Ctrl", StringComparison.Ordinal);
        }

        var result = new System.Text.StringBuilder(note.Length);
        var index = 0;
        while (true)
        {
            var at = note.IndexOf(Placeholder, index, StringComparison.Ordinal);
            if (at < 0)
            {
                result.Append(note, index, note.Length - index);
                return result.Replace("{cmd}", "⌘").ToString();
            }

            result.Append(note, index, at - index);
            index = at + Placeholder.Length;

            var end = index;
            while (end < note.Length && !char.IsWhiteSpace(note[end]))
            {
                end++;
            }

            result.Append(end - index <= 1 ? "⌘" : "⌘ + ");
        }
    }

    // "{chord:Run}" → the Run entry's primary chord on this scheme.
    private static string SpellChordPlaceholders(string note, ChordScheme scheme)
    {
        const string Open = "{chord:";
        var at = note.IndexOf(Open, StringComparison.Ordinal);
        while (at >= 0)
        {
            var end = note.IndexOf('}', at);
            var name = note[(at + Open.Length)..end];
            var label = CommandCatalog.Get(Enum.Parse<CommandId>(name)).PrimaryChordFor(scheme)?.Label(scheme)
                ?? throw new InvalidOperationException($"{name} has no chord to name in a gesture note.");
            note = string.Concat(note.AsSpan(0, at), label, note.AsSpan(end + 1));
            at = note.IndexOf(Open, at + label.Length, StringComparison.Ordinal);
        }

        return note;
    }

    /// <summary>
    /// The one-line shortcut text for the palette's trailing column and the
    /// docs: "Ctrl+Shift+F / Alt+Shift+F" or "⇧⌘F / ⌥⇧F", or null when there's
    /// nothing to show.
    /// </summary>
    public string? ShortcutLabel(ChordScheme scheme)
    {
        var parts = ChordsFor(scheme).Select(c => c.Label(scheme)).ToList();

        if (GestureNoteFor(scheme) is { Length: > 0 } note)
        {
            parts.Add(note);
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }
}

/// <summary>
/// One of a descriptor's <see cref="CommandDescriptor.MoreChords"/>.
/// </summary>
/// <param name="Chord">The gesture.</param>
/// <param name="Scheme">The one scheme it belongs to, or null for both.</param>
/// <param name="Primary">
/// Listed first on its scheme, ahead of the descriptor's own chord — the
/// platform's convention outranks the cross-platform default there.
/// </param>
public readonly record struct SchemeChord(Chord Chord, ChordScheme? Scheme = null, bool Primary = false)
{
    public bool AppliesTo(ChordScheme scheme) => Scheme is null || Scheme == scheme;
}
