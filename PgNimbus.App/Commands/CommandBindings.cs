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
        _ => Enum.TryParse<Key>(key.ToString(), out var parsed)
            ? parsed
            : throw new InvalidOperationException($"No Avalonia Key mapping for CommandKey.{key}."),
    };

    /// <summary>
    /// Resolves the abstract modifiers against the live Ctrl/Cmd scheme.
    /// <see cref="ChordModifiers.Control"/> stays literal Ctrl on every
    /// platform (completion's Ctrl+Space — Cmd+Space is Spotlight).
    /// </summary>
    public static KeyModifiers ToModifiers(ChordModifiers modifiers)
    {
        var result = KeyModifiers.None;
        if (modifiers.HasFlag(ChordModifiers.Command))
        {
            result |= Hotkeys.Command;
        }

        if (modifiers.HasFlag(ChordModifiers.Control))
        {
            result |= KeyModifiers.Control;
        }

        if (modifiers.HasFlag(ChordModifiers.Shift))
        {
            result |= KeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ChordModifiers.Alt))
        {
            result |= KeyModifiers.Alt;
        }

        return result;
    }

    public static KeyGesture ToGesture(Chord chord) => new(ToKey(chord.Key), ToModifiers(chord.Modifiers));

    /// <summary>The primary gesture for a command, or null when it has none (palette-only actions).</summary>
    public static KeyGesture? GestureFor(CommandId id) =>
        CommandCatalog.ChordFor(id) is { } chord ? ToGesture(chord) : null;

    /// <summary>
    /// Whether a key event is exactly this command's primary chord. Used by the
    /// panels and by <c>MainWindow.OnKeyDown</c>, where behaviour that a
    /// <c>KeyBinding</c> can't express (focus toggles, panels that bind the
    /// physical key themselves) still has to match the catalog's gesture.
    /// </summary>
    public static bool Matches(CommandId id, KeyEventArgs e)
    {
        if (CommandCatalog.ChordFor(id) is not { } chord)
        {
            return false;
        }

        return e.Key == ToKey(chord.Key) && e.KeyModifiers == ToModifiers(chord.Modifiers);
    }

    /// <summary>As <see cref="Matches(CommandId, KeyEventArgs)"/>, for a command's secondary gesture.</summary>
    public static bool MatchesAlt(CommandId id, KeyEventArgs e)
    {
        if (CommandCatalog.Get(id).AltChord is not { } chord)
        {
            return false;
        }

        return e.Key == ToKey(chord.Key) && e.KeyModifiers == ToModifiers(chord.Modifiers);
    }

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

        [CommandId.RefreshSchema] = vm => vm.RefreshSchemaCommand,
        [CommandId.ToggleSidebar] = vm => vm.ToggleSidebarCommand,
        [CommandId.ServerActivity] = vm => vm.ShowActivityCommand,
        [CommandId.DatabaseOverview] = vm => vm.ShowDatabaseOverviewCommand,
        [CommandId.NotifyMonitor] = vm => vm.ShowNotifyMonitorCommand,
        [CommandId.SecurityManager] = vm => vm.ShowSecurityCommand,
        [CommandId.SwitchConnection] = vm => vm.SwitchConnectionCommand,
        [CommandId.NewWindow] = vm => vm.OpenNewWindowCommand,
        [CommandId.ToggleTheme] = vm => vm.ToggleThemeCommand,
        [CommandId.Preferences] = vm => vm.ShowPreferencesCommand,
        [CommandId.ShortcutsWindow] = vm => vm.ShowShortcutsCommand,
    };
}
