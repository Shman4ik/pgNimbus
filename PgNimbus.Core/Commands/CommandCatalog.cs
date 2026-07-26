namespace PgNimbus.Core.Commands;

/// <summary>
/// The single source of truth for every command and documented keyboard
/// gesture in pgNimbus. The window's key bindings, the Ctrl+K palette, the
/// macOS native menu, the F1 cheat sheet and the published shortcut reference
/// are all projections of this list — adding a shortcut is one entry here, not
/// four hand-kept copies.
/// </summary>
public static class CommandCatalog
{
    private const ChordModifiers Cmd = ChordModifiers.Command;
    private const ChordModifiers CmdShift = ChordModifiers.Command | ChordModifiers.Shift;
    private const ChordModifiers Alt = ChordModifiers.Alt;
    private const ChordModifiers AltShift = ChordModifiers.Alt | ChordModifiers.Shift;
    private const ChordModifiers Shift = ChordModifiers.Shift;
    private const ChordModifiers LiteralCtrl = ChordModifiers.Control;

    private const CommandSurface Everywhere =
        CommandSurface.WindowBinding | CommandSurface.Palette | CommandSurface.CheatSheet;

    // Handled by a panel's own key handler (or MainWindow.OnKeyDown, where a
    // KeyBinding can't express the behaviour), so no window binding is emitted
    // — but it's still a palette action and a documented shortcut.
    private const CommandSurface PaletteAndSheet = CommandSurface.Palette | CommandSurface.CheatSheet;
    private const CommandSurface PaletteOnly = CommandSurface.Palette;
    private const CommandSurface SheetOnly = CommandSurface.CheatSheet;

    /// <summary>Every descriptor, in the order the cheat sheet and docs list them.</summary>
    public static IReadOnlyList<CommandDescriptor> All { get; } =
    [
        // ---------------------------------------------------------------- Query
        new()
        {
            Id = CommandId.Run,
            Title = "Run query",
            Category = CommandCategory.Query,
            Glyph = "▶",
            Chord = new(CommandKey.Enter, Cmd),
            AltChord = new(CommandKey.F5),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.RunStatementUnderCursor,
            Title = "Run just the statement under the cursor",
            Category = CommandCategory.Query,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Enter, Shift),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.Cancel,
            Title = "Cancel query",
            Category = CommandCategory.Query,
            Glyph = "■",
            Chord = new(CommandKey.Escape),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.Explain,
            Title = "Explain",
            CheatTitle = "Explain — estimated plan",
            Category = CommandCategory.Query,
            Glyph = "⚡",
            Chord = new(CommandKey.E, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ExplainAnalyze,
            Title = "Explain Analyze",
            CheatTitle = "Explain Analyze — runs the query",
            Category = CommandCategory.Query,
            Glyph = "⚡",
            Chord = new(CommandKey.E, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ImportPlan,
            Title = "Import query plan (paste EXPLAIN JSON or text)…",
            Category = CommandCategory.Query,
            Glyph = "⭳",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.FormatSql,
            Title = "Format SQL",
            CheatTitle = "Format the statement under the cursor",
            Category = CommandCategory.Query,
            Scope = CommandScope.Editor,
            Glyph = "❖",
            Chord = new(CommandKey.F, CmdShift),
            AltChord = new(CommandKey.F, AltShift),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.ExpandStar,
            Title = "Expand SELECT * into columns",
            Category = CommandCategory.Query,
            Glyph = "✳",
            // Shift+8 is "*" on most layouts — the gesture spells the command.
            Chord = new(CommandKey.D8, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.BeginTransaction,
            Title = "Begin transaction",
            Category = CommandCategory.Query,
            Glyph = "⛃",
            Chord = new(CommandKey.B, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.CommitTransaction,
            Title = "Commit transaction",
            Category = CommandCategory.Query,
            Glyph = "✓",
            // Enter confirms, Backspace undoes — the pair reads at a glance.
            Chord = new(CommandKey.Enter, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.RollbackTransaction,
            Title = "Rollback transaction",
            Category = CommandCategory.Query,
            Glyph = "↺",
            Chord = new(CommandKey.Backspace, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ToggleSafeMode,
            Title = "Toggle safe mode (stage grid changes, review & commit)",
            Category = CommandCategory.Query,
            Glyph = "⛨",
            // Deliberately no chord: flipping it by accident changes whether
            // grid edits hit the database immediately.
            Surfaces = PaletteOnly,
        },

        // --------------------------------------------------------- Tabs & files
        new()
        {
            Id = CommandId.NewTab,
            Title = "New query tab",
            Category = CommandCategory.Tabs,
            Glyph = "＋",
            Chord = new(CommandKey.T, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.CloseTab,
            Title = "Close tab",
            Category = CommandCategory.Tabs,
            Glyph = "✕",
            Chord = new(CommandKey.W, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.NextTab,
            Title = "Next tab",
            Category = CommandCategory.Tabs,
            Glyph = "›",
            Chord = new(CommandKey.PageDown, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.PreviousTab,
            Title = "Previous tab",
            Category = CommandCategory.Tabs,
            Glyph = "‹",
            Chord = new(CommandKey.PageUp, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.GoToTabByNumber,
            Title = "Go to tab 1…9",
            Category = CommandCategory.Tabs,
            // Nine bindings would be nine near-identical palette rows; the
            // window binds the digits in a loop and this documents the range.
            GestureNote = "{cmd}+1 … {cmd}+9",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.OpenFile,
            Title = "Open .sql file…",
            Category = CommandCategory.Tabs,
            Glyph = "↥",
            Chord = new(CommandKey.O, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SaveFile,
            Title = "Save tab to file",
            Category = CommandCategory.Tabs,
            Glyph = "↧",
            Chord = new(CommandKey.S, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SaveFileAs,
            Title = "Save tab as…",
            Category = CommandCategory.Tabs,
            Glyph = "↧",
            Chord = new(CommandKey.S, CmdShift),
            Surfaces = Everywhere,
        },

        // ----------------------------------------------------------- SQL editor
        new()
        {
            Id = CommandId.Completion,
            Title = "Autocomplete (also triggers while typing)",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            // Literal Ctrl on every platform: Cmd+Space is Spotlight on macOS.
            Chord = new(CommandKey.Space, LiteralCtrl),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.Find,
            Title = "Find in editor",
            Category = CommandCategory.Editor,
            Glyph = "⌕",
            Chord = new(CommandKey.F, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.FindReplace,
            Title = "Find & replace in editor",
            Category = CommandCategory.Editor,
            Glyph = "⌕",
            Chord = new(CommandKey.H, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.FindNextPrevious,
            Title = "Next / previous match",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.F3),
            AltChord = new(CommandKey.F3, Shift),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleLineComment,
            Title = "Toggle line comment",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Glyph = "—",
            Chord = new(CommandKey.Slash, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.DuplicateLine,
            Title = "Duplicate line (or selection)",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.D, CmdShift),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.MoveLineUp,
            Title = "Move line up",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Up, Alt),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.MoveLineDown,
            Title = "Move line down",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Down, Alt),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.DeleteLine,
            Title = "Delete whole line",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.D, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.UndoRedo,
            Title = "Undo / Redo",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Z, Cmd),
            AltChord = new(CommandKey.Y, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleWordWrap,
            Title = "Toggle word wrap (Notepad++ style)",
            CheatTitle = "Toggle word wrap",
            Category = CommandCategory.Editor,
            Glyph = "↩",
            Chord = new(CommandKey.Z, Alt),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ToggleAutoAlias,
            Title = "Toggle auto-alias tables (orders → orders o)",
            CheatTitle = "Toggle auto-alias on table completion",
            Category = CommandCategory.Editor,
            Glyph = "a",
            Chord = new(CommandKey.A, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ZoomEditor,
            Title = "Zoom font size in / out",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Plus, Cmd),
            AltChord = new(CommandKey.Minus, Cmd),
            GestureNote = "{cmd}+wheel",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ResetEditorZoom,
            Title = "Reset font size",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.D0, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.WordNavigation,
            Title = "Move to word start / end",
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Left, Cmd),
            AltChord = new(CommandKey.Right, Cmd),
            Surfaces = SheetOnly,
        },

        // --------------------------------------------------------- Results grid
        new()
        {
            Id = CommandId.EditCell,
            Title = "Edit selected cell",
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.F2),
            GestureNote = "double-click",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.CommitCellEdit,
            Title = "Commit / cancel cell edit",
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.Enter),
            AltChord = new(CommandKey.Escape),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.InspectCell,
            Title = "Inspect cell (full value, pretty-printed JSON)",
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.Space),
            GestureNote = "double-click (read-only) / context menu",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.CopySelection,
            Title = "Copy the selected cells",
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.C, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.DeleteRow,
            Title = "Delete the selected row (editable results)",
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.Delete),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.SetCellNull,
            Title = "Set cell to NULL",
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            GestureNote = "context menu",
            Surfaces = SheetOnly,
        },

        // ------------------------------------------------------ Navigation & app
        new()
        {
            Id = CommandId.CommandPalette,
            Title = "Command palette (jump to table / query / action)",
            Category = CommandCategory.Navigation,
            Chord = new(CommandKey.K, Cmd),
            AltChord = new(CommandKey.P, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.RefreshSchema,
            Title = "Refresh database & schema",
            Category = CommandCategory.Navigation,
            Glyph = "⟳",
            Chord = new(CommandKey.R, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.FocusSwap,
            Title = "Switch focus: editor ↔ results grid",
            Category = CommandCategory.Navigation,
            Chord = new(CommandKey.F6),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleSidebar,
            Title = "Toggle sidebar",
            CheatTitle = "Collapse / show the sidebar",
            Category = CommandCategory.Navigation,
            Glyph = "◫",
            Chord = new(CommandKey.B, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.PreviewTable,
            Title = "Preview a table (in the schema tree)",
            Category = CommandCategory.Navigation,
            GestureNote = "Double-click",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ServerActivity,
            Title = "Server activity",
            Category = CommandCategory.Navigation,
            Glyph = "∿",
            Chord = new(CommandKey.M, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.DatabaseOverview,
            Title = "Database overview (sizes, cache hit, unused indexes)",
            CheatTitle = "Database overview",
            Category = CommandCategory.Navigation,
            Glyph = "▦",
            Chord = new(CommandKey.G, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SwitchConnection,
            Title = "Switch connection…",
            Category = CommandCategory.Navigation,
            Glyph = "⇄",
            Chord = new(CommandKey.O, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.NewWindow,
            Title = "Open connection in new window…",
            Category = CommandCategory.Navigation,
            Glyph = "⧉",
            Chord = new(CommandKey.N, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ToggleTheme,
            Title = "Toggle light/dark theme",
            Category = CommandCategory.Navigation,
            Glyph = "◐",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.Preferences,
            Title = "Preferences…",
            Category = CommandCategory.Navigation,
            Glyph = "⚙",
            Chord = new(CommandKey.Comma, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ShortcutsWindow,
            Title = "Keyboard shortcuts",
            Category = CommandCategory.Navigation,
            Glyph = "?",
            Chord = new(CommandKey.F1),
            Surfaces = PaletteAndSheet,
        },
    ];

    private static readonly Dictionary<CommandId, CommandDescriptor> ById =
        All.ToDictionary(d => d.Id);

    /// <summary>The descriptor for <paramref name="id"/>; throws if the catalog has no such entry.</summary>
    public static CommandDescriptor Get(CommandId id) => ById[id];

    /// <summary>The primary chord for <paramref name="id"/>, if it has one.</summary>
    public static Chord? ChordFor(CommandId id) => ById[id].Chord;

    /// <summary>Everything that surfaces on <paramref name="surface"/>, in catalog order.</summary>
    public static IEnumerable<CommandDescriptor> On(CommandSurface surface) =>
        All.Where(d => d.In(surface));

    /// <summary>The cheat-sheet sections, in display order, with their rows.</summary>
    public static IEnumerable<(CommandCategory Category, IReadOnlyList<CommandDescriptor> Items)> CheatSheetSections()
    {
        foreach (var category in Enum.GetValues<CommandCategory>())
        {
            var items = All
                .Where(d => d.Category == category && d.In(CommandSurface.CheatSheet))
                .ToList();
            if (items.Count > 0)
            {
                yield return (category, items);
            }
        }
    }

    /// <summary>The heading a category is shown under.</summary>
    public static string CategoryTitle(CommandCategory category) => category switch
    {
        CommandCategory.Query => "Query",
        CommandCategory.Tabs => "Tabs & files",
        CommandCategory.Editor => "SQL editor",
        CommandCategory.Results => "Results grid",
        CommandCategory.Navigation => "Navigation",
        _ => category.ToString(),
    };
}
