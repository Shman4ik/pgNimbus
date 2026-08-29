using System.Text.Json;
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
/// per-connection snapshots.
/// </summary>
public sealed class WindowPlacementStore(string? filePath = null)
{
    private readonly string _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "window.json");

    /// <summary>
    /// The connection dialog's own placement file. Separate from the main
    /// window's: the two have unrelated sizes, and a resized dialog must not
    /// drag the main window's geometry along with it.
    /// </summary>
    public static WindowPlacementStore ForConnectionDialog() =>
        new(Path.Combine(AppDataPaths.GetRootDirectory(), "connection-window.json"));

    /// <summary>The saved placement, or null if none was ever saved (or the file is unreadable).</summary>
    public WindowPlacement? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        // A corrupt/empty/half-written file must never block startup - fall back
        // to "no saved placement" rather than throwing out of the startup path.
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, WindowPlacementJsonContext.Default.WindowPlacement);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(placement, WindowPlacementJsonContext.Default.WindowPlacement);
        File.WriteAllText(_filePath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowPlacement))]
internal sealed partial class WindowPlacementJsonContext : JsonSerializerContext;
