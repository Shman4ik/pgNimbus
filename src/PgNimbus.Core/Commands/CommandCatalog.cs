using PgNimbus.Core.Localization;

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
            TitleKey = StringKey.Command_Run,
            Category = CommandCategory.Query,
            Glyph = "▶",
            Chord = new(CommandKey.Enter, Cmd),
            AltChord = new(CommandKey.F5),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.RunStatementUnderCursor,
            TitleKey = StringKey.Command_RunStatementUnderCursor,
            Category = CommandCategory.Query,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Enter, Shift),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.Cancel,
            TitleKey = StringKey.Command_Cancel,
            Category = CommandCategory.Query,
            Glyph = "■",
            Chord = new(CommandKey.Escape),
            // ⌘. is the Mac's "stop" (Terminal, Xcode, every sheet's Cancel).
            MoreChords = [new(new(CommandKey.Period, Cmd), ChordScheme.Cmd)],
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.Explain,
            TitleKey = StringKey.Command_Explain,
            CheatTitleKey = StringKey.Command_Explain_Cheat,
            Category = CommandCategory.Query,
            Glyph = "⚡",
            Chord = new(CommandKey.E, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ExplainAnalyze,
            TitleKey = StringKey.Command_ExplainAnalyze,
            CheatTitleKey = StringKey.Command_ExplainAnalyze_Cheat,
            Category = CommandCategory.Query,
            Glyph = "⚡",
            Chord = new(CommandKey.E, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ImportPlan,
            TitleKey = StringKey.Command_ImportPlan,
            Category = CommandCategory.Query,
            Glyph = "⭳",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.QueryLab,
            TitleKey = StringKey.Command_QueryLab,
            CheatTitleKey = StringKey.Command_QueryLab_Cheat,
            Category = CommandCategory.Query,
            Glyph = "⇄",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.SavePlanToQueryLab,
            TitleKey = StringKey.Command_SavePlanToQueryLab,
            Category = CommandCategory.Query,
            Glyph = "⇄",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.ComparePlan,
            TitleKey = StringKey.Command_ComparePlan,
            Category = CommandCategory.Query,
            Glyph = "⇄",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.FormatSql,
            TitleKey = StringKey.Command_FormatSql,
            CheatTitleKey = StringKey.Command_FormatSql_Cheat,
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
            TitleKey = StringKey.Command_ExpandStar,
            Category = CommandCategory.Query,
            Glyph = "✳",
            // Shift+8 is "*" on most layouts — the gesture spells the command.
            Chord = new(CommandKey.D8, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.BeginTransaction,
            TitleKey = StringKey.Command_BeginTransaction,
            Category = CommandCategory.Query,
            Glyph = "⛃",
            Chord = new(CommandKey.B, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.CommitTransaction,
            TitleKey = StringKey.Command_CommitTransaction,
            Category = CommandCategory.Query,
            Glyph = "✓",
            // Enter confirms, Backspace undoes — the pair reads at a glance.
            Chord = new(CommandKey.Enter, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.RollbackTransaction,
            TitleKey = StringKey.Command_RollbackTransaction,
            Category = CommandCategory.Query,
            Glyph = "↺",
            Chord = new(CommandKey.Backspace, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ToggleSafeMode,
            TitleKey = StringKey.Command_ToggleSafeMode,
            Category = CommandCategory.Query,
            Glyph = "⛨",
            // Deliberately no chord: flipping it by accident changes whether
            // grid edits hit the database immediately.
            Surfaces = PaletteOnly,
        },
        // A production window's write switch (ROADMAP T3), also the title
        // bar's production label. No chord, for the same reason as safe mode;
        // the palette lists only the one that applies, and only in a
        // production window.
        new()
        {
            Id = CommandId.AllowWrites,
            TitleKey = StringKey.Command_AllowWrites,
            Category = CommandCategory.Query,
            Glyph = "✎",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.LockWrites,
            TitleKey = StringKey.Command_LockWrites,
            Category = CommandCategory.Query,
            Glyph = "⊘",
            Surfaces = PaletteOnly,
        },

        // --------------------------------------------------------- Tabs & files
        new()
        {
            Id = CommandId.NewTab,
            TitleKey = StringKey.Command_NewTab,
            Category = CommandCategory.Tabs,
            Glyph = "＋",
            Chord = new(CommandKey.T, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.CloseTab,
            TitleKey = StringKey.Command_CloseTab,
            Category = CommandCategory.Tabs,
            Glyph = "✕",
            Chord = new(CommandKey.W, Cmd),
            Surfaces = Everywhere,
        },
        // Close's undo, on the browser gesture because that is where everyone
        // learned it. The session keeps the last few closed tabs (text, name,
        // file, saved-query link, caret): a tab closed with unsaved SQL in it
        // used to be simply gone.
        new()
        {
            Id = CommandId.ReopenClosedTab,
            TitleKey = StringKey.Command_ReopenClosedTab,
            Category = CommandCategory.Tabs,
            Glyph = "↶",
            Chord = new(CommandKey.T, CmdShift),
            Surfaces = Everywhere,
        },
        // The bulk-close pair: no chord (three tab commands already own one),
        // reachable from the tab strip's right-click menu and the palette.
        new()
        {
            Id = CommandId.CloseOtherTabs,
            TitleKey = StringKey.Command_CloseOtherTabs,
            Category = CommandCategory.Tabs,
            Glyph = "✕",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.CloseTabsToTheRight,
            TitleKey = StringKey.Command_CloseTabsToTheRight,
            Category = CommandCategory.Tabs,
            Glyph = "✕",
            Surfaces = PaletteOnly,
        },
        // Renaming is a tab-strip verb like the closes: no chord (F2 is the
        // results grid's cell edit), reachable from the strip's right-click menu
        // and the palette.
        new()
        {
            Id = CommandId.RenameTab,
            TitleKey = StringKey.Command_RenameTab,
            Category = CommandCategory.Tabs,
            Glyph = "✎",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.NextTab,
            TitleKey = StringKey.Command_NextTab,
            Category = CommandCategory.Tabs,
            Glyph = "›",
            Chord = new(CommandKey.PageDown, Cmd),
            MoreChords =
            [
                // A Mac keyboard has no PgDn key (it is Fn+↓), so ⇧⌘] — what
                // Safari, Terminal and Xcode use — leads there.
                new(new(CommandKey.CloseBracket, CmdShift), ChordScheme.Cmd, Primary: true),
                // Literal Ctrl on both: browsers and editors agree on it everywhere.
                new(new(CommandKey.Tab, LiteralCtrl)),
            ],
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.PreviousTab,
            TitleKey = StringKey.Command_PreviousTab,
            Category = CommandCategory.Tabs,
            Glyph = "‹",
            Chord = new(CommandKey.PageUp, Cmd),
            MoreChords =
            [
                new(new(CommandKey.OpenBracket, CmdShift), ChordScheme.Cmd, Primary: true),
                new(new(CommandKey.Tab, LiteralCtrl | ChordModifiers.Shift)),
            ],
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.GoToTabByNumber,
            TitleKey = StringKey.Command_GoToTabByNumber,
            Category = CommandCategory.Tabs,
            // Nine bindings would be nine near-identical palette rows; the
            // window binds the digits in a loop and this documents the range.
            GestureNoteKey = StringKey.Command_GoToTabByNumber_Note,
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.OpenFile,
            TitleKey = StringKey.Command_OpenFile,
            Category = CommandCategory.Tabs,
            Glyph = "↥",
            Chord = new(CommandKey.O, Cmd),
            Surfaces = Everywhere,
        },
        // The two chorded saves are deliberately *smart* rather than each
        // owning one destination, because pgNimbus has two of them — the
        // Saved Queries list and a .sql file on disk — and Ctrl+S went to the
        // file one alone. Users pressed the obvious gesture, got a file
        // picker, and never found the list sitting in the sidebar in front of
        // them; "how do I save a query" was the single most reported piece of
        // confusion. So Ctrl+S now follows the tab: one opened from a file
        // saves back to it, a scratch tab saves to the list. The two explicit
        // rows below name a destination outright, for the palette, so neither
        // destination is reachable only through a guess about the tab.
        new()
        {
            Id = CommandId.Save,
            TitleKey = StringKey.Command_Save,
            CheatTitleKey = StringKey.Command_Save_Cheat,
            Category = CommandCategory.Tabs,
            Glyph = "↧",
            Chord = new(CommandKey.S, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SaveAs,
            TitleKey = StringKey.Command_SaveAs,
            CheatTitleKey = StringKey.Command_SaveAs_Cheat,
            Category = CommandCategory.Tabs,
            Glyph = "↧",
            Chord = new(CommandKey.S, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SaveQuery,
            TitleKey = StringKey.Command_SaveQuery,
            Category = CommandCategory.Tabs,
            Glyph = "☆",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.SaveFile,
            TitleKey = StringKey.Command_SaveFile,
            Category = CommandCategory.Tabs,
            Glyph = "↧",
            Surfaces = PaletteOnly,
        },

        // ----------------------------------------------------------- SQL editor
        new()
        {
            Id = CommandId.Completion,
            TitleKey = StringKey.Command_Completion,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            // Literal Ctrl on every platform: Cmd+Space is Spotlight on macOS.
            Chord = new(CommandKey.Space, LiteralCtrl),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ParameterHints,
            TitleKey = StringKey.Command_ParameterHints,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            // Literal Ctrl, like completion: Cmd+Shift+Space is taken on macOS.
            Chord = new(CommandKey.Space, LiteralCtrl | ChordModifiers.Shift),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.Find,
            TitleKey = StringKey.Command_Find,
            Category = CommandCategory.Editor,
            Glyph = "⌕",
            Chord = new(CommandKey.F, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.FindReplace,
            TitleKey = StringKey.Command_FindReplace,
            Category = CommandCategory.Editor,
            Glyph = "⌕",
            Chord = new(CommandKey.H, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.FindNextPrevious,
            TitleKey = StringKey.Command_FindNextPrevious,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.F3),
            AltChord = new(CommandKey.F3, Shift),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleLineComment,
            TitleKey = StringKey.Command_ToggleLineComment,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Glyph = "—",
            Chord = new(CommandKey.Slash, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.DuplicateLine,
            TitleKey = StringKey.Command_DuplicateLine,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.D, CmdShift),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.MoveLineUp,
            TitleKey = StringKey.Command_MoveLineUp,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Up, Alt),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.MoveLineDown,
            TitleKey = StringKey.Command_MoveLineDown,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Down, Alt),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.DeleteLine,
            TitleKey = StringKey.Command_DeleteLine,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.D, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.UndoRedo,
            TitleKey = StringKey.Command_UndoRedo,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Z, Cmd),
            AltChord = new(CommandKey.Y, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleWordWrap,
            TitleKey = StringKey.Command_ToggleWordWrap,
            Category = CommandCategory.Editor,
            Glyph = "↩",
            Chord = new(CommandKey.Z, Alt),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ToggleAutoAlias,
            TitleKey = StringKey.Command_ToggleAutoAlias,
            CheatTitleKey = StringKey.Command_ToggleAutoAlias_Cheat,
            Category = CommandCategory.Editor,
            Glyph = "a",
            Chord = new(CommandKey.A, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ZoomEditor,
            TitleKey = StringKey.Command_ZoomEditor,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.Plus, Cmd),
            AltChord = new(CommandKey.Minus, Cmd),
            GestureNoteKey = StringKey.Command_ZoomEditor_Note,
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ResetEditorZoom,
            TitleKey = StringKey.Command_ResetEditorZoom,
            Category = CommandCategory.Editor,
            Scope = CommandScope.Editor,
            Chord = new(CommandKey.D0, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.WordNavigation,
            TitleKey = StringKey.Command_WordNavigation,
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
            TitleKey = StringKey.Command_EditCell,
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.F2),
            GestureNoteKey = StringKey.Command_EditCell_Note,
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.CommitCellEdit,
            TitleKey = StringKey.Command_CommitCellEdit,
            CheatTitleKey = StringKey.Command_CommitCellEdit_Cheat,
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.Enter),
            AltChord = new(CommandKey.Escape),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.InspectCell,
            TitleKey = StringKey.Command_InspectCell,
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.Space),
            GestureNoteKey = StringKey.Command_InspectCell_Note,
            Surfaces = SheetOnly,
        },
        new()
        {
            // No chord of its own: in the inspector's editor the Run chord
            // commits the value, the way it runs what is typed in the SQL
            // editor, so the gesture is a note that borrows Run's chord rather
            // than a second Ctrl/Cmd+Enter the catalog test would reject as
            // shadowing the global one. It used to reach the window and run
            // the query behind the overlay.
            Id = CommandId.SaveInspectedValue,
            TitleKey = StringKey.Command_SaveInspectedValue,
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            GestureNoteKey = StringKey.Command_SaveInspectedValue_Note,
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.CopySelection,
            TitleKey = StringKey.Command_CopySelection,
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.C, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.DeleteRow,
            TitleKey = StringKey.Command_DeleteRow,
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            Chord = new(CommandKey.Delete),
            Surfaces = SheetOnly,
        },
        new()
        {
            // Global, not Results: the sidebar is a place focus lives in its
            // own right (Tab through the fields), and the chord has to close
            // it from there as well as open it from the grid.
            Id = CommandId.RowDetails,
            TitleKey = StringKey.Command_RowDetails,
            CheatTitleKey = StringKey.Command_RowDetails_Cheat,
            Category = CommandCategory.Results,
            Glyph = "▤",
            Chord = new(CommandKey.I, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            // No chord of its own: in the grid while browsing, the Find chord
            // means "filter these rows" (the grid has nothing else to find in),
            // so the gesture is documented here as a note rather than as a
            // second Ctrl/Cmd+F that would clash with the global one.
            Id = CommandId.FilterRows,
            TitleKey = StringKey.Command_FilterRows,
            Category = CommandCategory.Results,
            Glyph = "▽",
            GestureNoteKey = StringKey.Command_FilterRows_Note,
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.SetCellNull,
            TitleKey = StringKey.Command_SetCellNull,
            Category = CommandCategory.Results,
            Scope = CommandScope.Results,
            GestureNoteKey = StringKey.Command_SetCellNull_Note,
            Surfaces = SheetOnly,
        },

        // ------------------------------------------------------ Navigation & app
        new()
        {
            Id = CommandId.CommandPalette,
            TitleKey = StringKey.Command_CommandPalette,
            Category = CommandCategory.Navigation,
            Chord = new(CommandKey.K, Cmd),
            AltChord = new(CommandKey.P, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.RefreshSchema,
            TitleKey = StringKey.Command_RefreshSchema,
            Category = CommandCategory.Navigation,
            Glyph = "⟳",
            Chord = new(CommandKey.R, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.FocusSwap,
            TitleKey = StringKey.Command_FocusSwap,
            Category = CommandCategory.Navigation,
            Chord = new(CommandKey.F6),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleSidebar,
            TitleKey = StringKey.Command_ToggleSidebar,
            CheatTitleKey = StringKey.Command_ToggleSidebar_Cheat,
            Category = CommandCategory.Navigation,
            Glyph = "◫",
            Chord = new(CommandKey.B, Cmd),
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.PreviewTable,
            TitleKey = StringKey.Command_PreviewTable,
            Category = CommandCategory.Navigation,
            GestureNoteKey = StringKey.Command_PreviewTable_Note,
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ServerActivity,
            TitleKey = StringKey.Command_ServerActivity,
            Category = CommandCategory.Navigation,
            Glyph = "∿",
            Chord = new(CommandKey.M, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.DatabaseOverview,
            TitleKey = StringKey.Command_DatabaseOverview,
            CheatTitleKey = StringKey.Command_DatabaseOverview_Cheat,
            Category = CommandCategory.Navigation,
            Glyph = "▦",
            Chord = new(CommandKey.G, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SlowQueries,
            TitleKey = StringKey.Command_SlowQueries,
            CheatTitleKey = StringKey.Command_SlowQueries_Cheat,
            Category = CommandCategory.Navigation,
            Glyph = "⧗",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.NotifyMonitor,
            TitleKey = StringKey.Command_NotifyMonitor,
            SearchKey = StringKey.Command_NotifyMonitor_Search,
            Category = CommandCategory.Navigation,
            Glyph = "⚑",
            Chord = new(CommandKey.L, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SecurityManager,
            TitleKey = StringKey.Command_SecurityManager,
            CheatTitleKey = StringKey.Command_SecurityManager_Cheat,
            Category = CommandCategory.Navigation,
            Glyph = "⚿",
            Chord = new(CommandKey.U, CmdShift),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.SwitchConnection,
            TitleKey = StringKey.Command_SwitchConnection,
            Category = CommandCategory.Navigation,
            Glyph = "⇄",
            Chord = new(CommandKey.O, CmdShift),
            Surfaces = Everywhere,
        },
        // Another database of the same server, in this window: the list the
        // title bar's database name opens. Palette only, no chord and no ☰ item
        // (UI design rule 1): the breadcrumb is its visible home. Listed only in
        // a window that can switch (MainViewModel.CanSwitchDatabase).
        new()
        {
            Id = CommandId.SwitchDatabase,
            TitleKey = StringKey.Command_SwitchDatabase,
            Category = CommandCategory.Navigation,
            Glyph = "⇆",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.NewWindow,
            TitleKey = StringKey.Command_NewWindow,
            Category = CommandCategory.Navigation,
            Glyph = "⧉",
            Chord = new(CommandKey.N, CmdShift),
            Surfaces = Everywhere,
        },
        // pg_dump of the window's database. No chord: a backup is a deliberate,
        // occasional act, reached from the palette, the ☰ menu and the macOS
        // File menu; a schema's or a table's own is on its right-click menu.
        new()
        {
            Id = CommandId.BackupDatabase,
            TitleKey = StringKey.Command_BackupDatabase,
            Category = CommandCategory.Navigation,
            Glyph = "⤓",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.RestoreBackup,
            TitleKey = StringKey.Command_RestoreBackup,
            Category = CommandCategory.Navigation,
            Glyph = "⤒",
            Surfaces = PaletteOnly,
        },
        // A relation's Maintenance submenu (Analyze, Vacuum, Truncate…, Rename…,
        // Drop…). They act on the relation selected in the schema tree, else on
        // the active tab's browsed table, and the palette lists them only when
        // there is one and the action applies to its kind. No chords and no ☰
        // items: maintenance is occasional, and two of these destroy data.
        new()
        {
            Id = CommandId.AnalyzeTable,
            TitleKey = StringKey.Command_AnalyzeTable,
            Category = CommandCategory.Navigation,
            Glyph = "∑",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.VacuumTable,
            TitleKey = StringKey.Command_VacuumTable,
            Category = CommandCategory.Navigation,
            Glyph = "⟳",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.TruncateTable,
            TitleKey = StringKey.Command_TruncateTable,
            Category = CommandCategory.Navigation,
            Glyph = "⌀",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.RenameTable,
            TitleKey = StringKey.Command_RenameTable,
            Category = CommandCategory.Navigation,
            Glyph = "✐",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.DropTable,
            TitleKey = StringKey.Command_DropTable,
            Category = CommandCategory.Navigation,
            Glyph = "✕",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.ToggleTheme,
            TitleKey = StringKey.Command_ToggleTheme,
            Category = CommandCategory.Navigation,
            Glyph = "◐",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.Preferences,
            TitleKey = StringKey.Command_Preferences,
            Category = CommandCategory.Navigation,
            Glyph = "⚙",
            Chord = new(CommandKey.Comma, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ShortcutsWindow,
            TitleKey = StringKey.Command_ShortcutsWindow,
            Category = CommandCategory.Navigation,
            Glyph = "?",
            Chord = new(CommandKey.F1),
            // ⌘? (⇧⌘/) is a Mac's Help key, and F1 there needs Fn. Not ⌘/:
            // that is the editor's line comment.
            MoreChords = [new(new(CommandKey.Slash, CmdShift), ChordScheme.Cmd, Primary: true)],
            Surfaces = PaletteAndSheet,
        },
        // Opens the Store's rate-and-review page. The palette lists it only when
        // the app runs as the Store MSIX (MainViewModel.CanRateInStore), beside
        // the ☰ menu item and the About box's link. No chord and no prompt: it
        // is there for whoever looks for it, and asks nobody (UI design rule 1).
        new()
        {
            Id = CommandId.RateInStore,
            TitleKey = StringKey.Command_RateInStore,
            Category = CommandCategory.Navigation,
            Glyph = "☆",
            Surfaces = PaletteOnly,
        },
    ];

    private static readonly Dictionary<CommandId, CommandDescriptor> ById =
        All.ToDictionary(d => d.Id);

    /// <summary>The descriptor for <paramref name="id"/>; throws if the catalog has no such entry.</summary>
    public static CommandDescriptor Get(CommandId id) => ById[id];

    /// <summary>The chord <paramref name="id"/> is named by on <paramref name="scheme"/>, if it has one.</summary>
    public static Chord? ChordFor(CommandId id, ChordScheme scheme) => ById[id].PrimaryChordFor(scheme);

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
        CommandCategory.Query => Strings.Command_Category_Query,
        CommandCategory.Tabs => Strings.Command_Category_Tabs,
        CommandCategory.Editor => Strings.Command_Category_Editor,
        CommandCategory.Results => Strings.Command_Category_Results,
        CommandCategory.Navigation => Strings.Command_Category_Navigation,
        _ => category.ToString(),
    };
}
