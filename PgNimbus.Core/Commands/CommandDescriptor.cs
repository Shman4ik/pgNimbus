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
    CopySelection,
    DeleteRow,

    // --- Navigation & app ---
    CommandPalette,
    RefreshSchema,
    FocusSwap,
    ToggleSidebar,
    PreviewTable,
    ServerActivity,
    DatabaseOverview,
    SecurityManager,
    SwitchConnection,
    NewWindow,
    ToggleTheme,
    Preferences,
    ShortcutsWindow,
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

    /// <summary>A second accepted combination (F5 for Run, Ctrl+P for the palette).</summary>
    public Chord? AltChord { get; init; }

    /// <summary>
    /// Free text for gestures that aren't a chord at all ("Double-click",
    /// "context menu") or a range too wide to enumerate ("{cmd}+1 … {cmd}+9").
    /// Rendered as quiet text next to (or instead of) the key caps; "{cmd}" is
    /// substituted with the resolved Ctrl/Cmd label.
    /// </summary>
    public string? GestureNote { get; init; }

    public CommandSurface Surfaces { get; init; } = CommandSurface.CheatSheet;

    /// <summary>The label to show outside the palette.</summary>
    public string DisplayName => CheatTitle ?? Title;

    public bool In(CommandSurface surface) => Surfaces.HasFlag(surface);

    /// <summary><see cref="GestureNote"/> with "{cmd}" resolved to "Ctrl" or "Cmd".</summary>
    public string? GestureNoteFor(string commandLabel) =>
        GestureNote?.Replace("{cmd}", commandLabel, StringComparison.Ordinal);

    /// <summary>
    /// The one-line shortcut text for the palette's trailing column:
    /// "Ctrl+Shift+F / Alt+Shift+F", or null when there's nothing to show.
    /// </summary>
    public string? ShortcutLabel(string commandLabel)
    {
        var parts = new List<string>(3);
        if (Chord is { } chord)
        {
            parts.Add(chord.Label(commandLabel));
        }

        if (AltChord is { } alt)
        {
            parts.Add(alt.Label(commandLabel));
        }

        if (GestureNoteFor(commandLabel) is { Length: > 0 } note)
        {
            parts.Add(note);
        }

        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }
}
