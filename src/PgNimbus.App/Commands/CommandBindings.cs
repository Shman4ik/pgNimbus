using System.Windows.Input;
using Avalonia.Input;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;

namespace PgNimbus.App;

/// <summary>
/// The App-side half of <see cref="CommandCatalog"/>: it turns the catalog's
/// UI-free descriptors into real Avalonia gestures and resolves each
/// <see cref="CommandId"/> to the view-model command that runs it. Everything
/// that used to hardcode a gesture — the window's key bindings, the macOS
/// menu, the palette rows, the F1 sheet — goes through here instead, so a
/// shortcut is stated once in Core and rendered everywhere from that.
/// </summary>
public static class CommandBindings
{
    static CommandBindings()
    {
        // The catalog lives in Core and can't see this map, so the "every
        // invocable command has a resolver" half of the contract is checked
        // here, once, at first use — a missing entry fails loudly at startup
        // instead of as a dead key the user reports months later.
        var unmapped = CommandCatalog.All
            .Where(d => (d.In(CommandSurface.WindowBinding) || d.In(CommandSurface.Palette))
                        && !Resolvers.ContainsKey(d.Id))
            .Select(d => d.Id.ToString())
            .ToList();

        if (unmapped.Count > 0)
        {
            throw new InvalidOperationException(
                "CommandCatalog entries with no resolver in CommandBindings: " + string.Join(", ", unmapped));
        }
    }

    /// <summary>
    /// Maps a catalog key onto Avalonia's. All but a handful share a name with
    /// their <see cref="Key"/> counterpart, so only the exceptions are listed;
    /// an unmapped key throws loudly at startup rather than silently producing
    /// a dead shortcut.
    /// </summary>
    public static Key ToKey(CommandKey key) => key switch
    {
        CommandKey.Backspace => Key.Back,
        CommandKey.Comma => Key.OemComma,
        CommandKey.Slash => Key.OemQuestion,
        CommandKey.Plus => Key.OemPlus,
        CommandKey.Minus => Key.OemMinus,
        CommandKey.Period => Key.OemPeriod,
        CommandKey.OpenBracket => Key.OemOpenBrackets,
        CommandKey.CloseBracket => Key.OemCloseBrackets,
        _ => Enum.TryParse<Key>(key.ToString(), out var parsed)
            ? parsed
            : throw new InvalidOperationException($"No Avalonia Key mapping for CommandKey.{key}."),
    };

    /// <summary>
    /// Resolves the abstract modifiers against the live Ctrl/Cmd scheme.
    /// <see cref="ChordModifiers.Control"/> stays literal Ctrl on every
    /// platform (completion's Ctrl+Space — Cmd+Space is Spotlight).
    /// </summary>
    public static KeyModifiers ToModifiers(ChordModifiers modifiers) =>
        ToModifiers(new Chord(CommandKey.A, modifiers).Resolve(Hotkeys.Scheme).Modifiers);

    public static KeyModifiers ToModifiers(PhysicalModifiers modifiers)
    {
        var result = KeyModifiers.None;
        if (modifiers.HasFlag(PhysicalModifiers.Control))
        {
            result |= KeyModifiers.Control;
        }

        if (modifiers.HasFlag(PhysicalModifiers.Alt))
        {
            result |= KeyModifiers.Alt;
        }

        if (modifiers.HasFlag(PhysicalModifiers.Shift))
        {
            result |= KeyModifiers.Shift;
        }

        if (modifiers.HasFlag(PhysicalModifiers.Meta))
        {
            result |= KeyModifiers.Meta;
        }

        return result;
    }

    public static KeyGesture ToGesture(Chord chord) => new(ToKey(chord.Key), ToModifiers(chord.Modifiers));

    /// <summary>
    /// The gesture a command is named by in the live scheme — what a menu item
    /// or tooltip shows — or null when it has none (palette-only actions). On
    /// the Cmd scheme that can be the Mac's own chord (⇧⌘] for the next tab)
    /// rather than the catalog's cross-platform one.
    /// </summary>
    public static KeyGesture? GestureFor(CommandId id) =>
        CommandCatalog.ChordFor(id, Hotkeys.Scheme) is { } chord ? ToGesture(chord) : null;

    /// <summary>Every gesture that invokes a command in the live scheme, primary first.</summary>
    public static IEnumerable<KeyGesture> GesturesFor(CommandId id) =>
        CommandCatalog.Get(id).ChordsFor(Hotkeys.Scheme).Select(ToGesture);

    /// <summary>Every chord of a command, primary first, spelled for the live scheme: "⌘↩", "F5".</summary>
    public static IReadOnlyList<string> LabelsFor(CommandId id)
    {
        var scheme = Hotkeys.Scheme;
        return CommandCatalog.Get(id).ChordsFor(scheme).Select(chord => chord.Label(scheme)).ToList();
    }

    /// <summary>"Ctrl+K" or "⌘K": a command's primary chord, spelled for the live scheme.</summary>
    public static string LabelFor(CommandId id) =>
        CommandCatalog.ChordFor(id, Hotkeys.Scheme)?.Label(Hotkeys.Scheme) ?? string.Empty;

    /// <summary>
    /// Whether a key event is one of this command's gestures: its chord and
    /// every synonym the live scheme adds (⌘. for Cancel, ⌘? beside F1), but
    /// not <see cref="CommandDescriptor.AltChord"/>, which can be a different
    /// action (Escape beside Enter's commit) — see <see cref="MatchesAlt"/>.
    /// Used by the panels and by <c>MainWindow.OnKeyDown</c>, where behaviour
    /// that a <c>KeyBinding</c> can't express (focus toggles, panels that bind
    /// the physical key themselves) still has to match the catalog's gesture.
    /// </summary>
    public static bool Matches(CommandId id, KeyEventArgs e) =>
        CommandCatalog.Get(id).SynonymsFor(Hotkeys.Scheme).Any(chord => Matches(chord, e));

    /// <summary>As <see cref="Matches(CommandId, KeyEventArgs)"/>, for a command's secondary gesture.</summary>
    public static bool MatchesAlt(CommandId id, KeyEventArgs e) =>
        CommandCatalog.Get(id).AltChord is { } chord && Matches(chord, e);

    private static bool Matches(Chord chord, KeyEventArgs e) =>
        e.Key == ToKey(chord.Key) && e.KeyModifiers == ToModifiers(chord.Modifiers);

    /// <summary>The command a catalog entry invokes; null while its target isn't available yet.</summary>
    public static ICommand? Resolve(CommandId id, MainViewModel vm) =>
        Resolvers.TryGetValue(id, out var resolve)
            ? resolve(vm)
            : throw new InvalidOperationException(
                $"CommandId.{id} surfaces in the key bindings or the palette but has no resolver in CommandBindings.");

    // Deliberately a lookup rather than a switch on the whole enum: the catalog
    // also holds documentation-only rows (Ctrl+Z, double-click to preview, …)
    // that have no view-model command, and those must never reach Resolve.
    private static readonly Dictionary<CommandId, Func<MainViewModel, ICommand?>> Resolvers = new()
    {
        // ActiveTab settles after construction and changes on every tab switch,
        // so these resolve through it each time rather than being captured.
        [CommandId.Run] = vm => vm.ActiveTab?.RunCommand,
        [CommandId.Cancel] = vm => vm.ActiveTab?.CancelCommand,
        [CommandId.Explain] = vm => vm.ActiveTab?.ExplainCommand,
        [CommandId.ExplainAnalyze] = vm => vm.ActiveTab?.ExplainAnalyzeCommand,

        [CommandId.ImportPlan] = vm => vm.ImportPlanCommand,
        [CommandId.FormatSql] = vm => vm.FormatSqlCommand,
        [CommandId.ExpandStar] = vm => vm.ExpandStarCommand,
        [CommandId.BeginTransaction] = vm => vm.BeginTransactionCommand,
        [CommandId.CommitTransaction] = vm => vm.CommitTransactionCommand,
        [CommandId.RollbackTransaction] = vm => vm.RollbackTransactionCommand,
        [CommandId.ToggleSafeMode] = vm => vm.ToggleSafeModeCommand,

        [CommandId.NewTab] = vm => vm.AddTabCommand,
        [CommandId.CloseTab] = vm => vm.CloseTabCommand,
        [CommandId.ReopenClosedTab] = vm => vm.ReopenClosedTabCommand,
        [CommandId.CloseOtherTabs] = vm => vm.CloseOtherTabsCommand,
        [CommandId.CloseTabsToTheRight] = vm => vm.CloseTabsToTheRightCommand,
        [CommandId.RenameTab] = vm => vm.RenameTabCommand,
        [CommandId.NextTab] = vm => vm.NextTabCommand,
        [CommandId.PreviousTab] = vm => vm.PreviousTabCommand,
        [CommandId.OpenFile] = vm => vm.OpenFileCommand,
        [CommandId.Save] = vm => vm.SaveCommand,
        [CommandId.SaveAs] = vm => vm.SaveAsCommand,
        [CommandId.SaveQuery] = vm => vm.SaveQueryCommand,
        [CommandId.SaveFile] = vm => vm.SaveFileCommand,

        [CommandId.Find] = vm => vm.FindCommand,
        [CommandId.FindReplace] = vm => vm.FindReplaceCommand,
        [CommandId.ToggleLineComment] = vm => vm.ToggleLineCommentCommand,
        [CommandId.ToggleWordWrap] = vm => vm.ToggleWordWrapCommand,
        [CommandId.ToggleAutoAlias] = vm => vm.ToggleAutoAliasCommand,

        [CommandId.RowDetails] = vm => vm.ToggleRowDetailsCommand,
        [CommandId.FilterRows] = vm => vm.FilterRowsCommand,

        [CommandId.RefreshSchema] = vm => vm.RefreshSchemaCommand,
        [CommandId.ToggleSidebar] = vm => vm.ToggleSidebarCommand,
        [CommandId.ServerActivity] = vm => vm.ShowActivityCommand,
        [CommandId.DatabaseOverview] = vm => vm.ShowDatabaseOverviewCommand,
        [CommandId.SlowQueries] = vm => vm.ShowSlowQueriesCommand,
        [CommandId.NotifyMonitor] = vm => vm.ShowNotifyMonitorCommand,
        [CommandId.SecurityManager] = vm => vm.ShowSecurityCommand,
        [CommandId.SwitchConnection] = vm => vm.SwitchConnectionCommand,
        [CommandId.NewWindow] = vm => vm.OpenNewWindowCommand,
        [CommandId.BackupDatabase] = vm => vm.BackupDatabaseCommand,
        [CommandId.RestoreBackup] = vm => vm.RestoreBackupCommand,
        [CommandId.ToggleTheme] = vm => vm.ToggleThemeCommand,
        [CommandId.Preferences] = vm => vm.ShowPreferencesCommand,
        [CommandId.ShortcutsWindow] = vm => vm.ShowShortcutsCommand,
    };
}
