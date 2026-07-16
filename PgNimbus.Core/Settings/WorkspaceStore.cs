using System.Text.Json;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Settings;

/// <summary>
/// A single saved tab: its SQL text, for a titled tab (e.g. a table/function
/// "source" tab) the title override, and, for a file-backed tab, the local
/// path it's associated with. <paramref name="FilePath"/> is null for a
/// scratch tab; a workspace.json written before this field existed still
/// deserializes with it defaulting to null.
/// </summary>
public sealed record WorkspaceTab(string Sql, string? Title = null, string? FilePath = null);

/// <summary>A saved snapshot of one connection's open tabs, most-recently-saved entries kept first in the store.</summary>
public sealed record WorkspaceEntry(string Connection, DateTimeOffset SavedAt, List<WorkspaceTab> Tabs, int ActiveTabIndex = 0);

/// <summary>Persists the last <see cref="MaxEntries"/> per-connection workspaces, most recent first.</summary>
public sealed class WorkspaceStore
{
    private const int MaxEntries = 20;

    private readonly string _filePath;

    public WorkspaceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "workspace.json");
    }

    private IReadOnlyList<WorkspaceEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        // A corrupt/empty/half-written file must never block startup - fall back
        // to an empty list rather than throwing out of the constructor path.
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.ListWorkspaceEntry) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The most recently saved workspace for <paramref name="connection"/>, or null if none was ever saved.</summary>
    public WorkspaceEntry? GetEntry(string connection) =>
        Load().FirstOrDefault(e => string.Equals(e.Connection, connection, StringComparison.Ordinal));

    /// <summary>Replaces the saved workspace for <paramref name="connection"/> with the current tabs.</summary>
    public void Save(string connection, IReadOnlyList<WorkspaceTab> tabs, int activeTabIndex)
    {
        var entries = Load().ToList();
        entries.RemoveAll(e => string.Equals(e.Connection, connection, StringComparison.Ordinal));
        entries.Insert(0, new WorkspaceEntry(connection, DateTimeOffset.UtcNow, tabs.ToList(), activeTabIndex));

        // Trim oldest-first - the list is most-recent-first, so drop from the end.
        for (var i = entries.Count - 1; i >= 0 && entries.Count > MaxEntries; i--)
        {
            entries.RemoveAt(i);
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entries, WorkspaceJsonContext.Default.ListWorkspaceEntry);
        File.WriteAllText(_filePath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<WorkspaceEntry>))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
