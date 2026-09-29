using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Settings;

/// <summary>
/// The main window's last known placement. <see cref="X"/>/<see cref="Y"/> are
/// physical screen pixels (Avalonia's <c>Window.Position</c> space) while
/// <see cref="Width"/>/<see cref="Height"/> are DIPs (<c>Window.Width</c>/
/// <c>Height</c> space) — that split mirrors Avalonia's own API, so the App
/// round-trips values without converting. Kept as plain numbers so
/// <c>PgNimbus.Core</c> stays free of UI-framework types; validating the
/// placement against the live monitor layout is the App's job on restore.
/// When <see cref="IsMaximized"/> is true, the other fields still hold the
/// last <em>normal</em> bounds — what the window should return to on
/// unmaximize — not the maximized rect.
/// </summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized);

/// <summary>
/// Persists a window's placement across sessions (<c>window.json</c> for the
/// main window, next to <c>workspace.json</c> — window geometry is session
/// state like the restored workspace, not a user preference like
/// <see cref="AppSettings"/>). One placement per file: with several main
/// windows open, the last one to close wins, same as the workspace store's
/// per-connection snapshots. Reads and writes go through <see cref="AppDataFile"/>.
/// </summary>
public sealed class WindowPlacementStore(string? filePath = null)
{
    private readonly string? _filePath = filePath ?? AppDataPaths.Resolve("window.json");

    /// <summary>
    /// The connection dialog's own placement file. Separate from the main
    /// window's: the two have unrelated sizes, and a resized dialog must not
    /// drag the main window's geometry along with it.
    /// </summary>
    public static WindowPlacementStore ForConnectionDialog() =>
        new(AppDataPaths.Resolve("connection-window.json"));

    /// <summary>The saved placement, or null if none was ever saved, the file is unreadable, or it cannot be parsed (then it is moved aside first).</summary>
    public WindowPlacement? Load() =>
        AppDataFile.ReadJson(_filePath, WindowPlacementJsonContext.Default.WindowPlacement);

    public void Save(WindowPlacement placement) =>
        AppDataFile.WriteJson(_filePath, placement, WindowPlacementJsonContext.Default.WindowPlacement);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowPlacement))]
internal sealed partial class WindowPlacementJsonContext : JsonSerializerContext;
