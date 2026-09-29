using System.Text.Json;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Security;

namespace PgNimbus.Core.Settings;

/// <summary>
/// A single saved tab: its SQL text, for a titled tab (e.g. a table/function
/// "source" tab) the title override, for a file-backed tab the local path it's
/// associated with, and for a tab saved into the Saved Queries list the id of
/// that entry — which is what lets a restored tab keep overwriting its own
/// saved query instead of minting a duplicate on the next Save.
/// <paramref name="FilePath"/> and <paramref name="SavedQueryId"/> are null for
/// a scratch tab; a workspace.json written before either field existed still
/// deserializes with it defaulting to null.
/// <paramref name="BrowseSchema"/>/<paramref name="BrowseTable"/> name the table a
/// tab was opened to browse, so after a restart a run of its page query can
/// resume browse mode (filter chips included) instead of the tab being a plain
/// query for good. Only the name is kept: the columns are read fresh from the
/// catalog when it's needed, since the table may have changed in between.
/// </summary>
public sealed record WorkspaceTab(
    string Sql,
    string? Title = null,
    string? FilePath = null,
    Guid? SavedQueryId = null,
    string? BrowseSchema = null,
    string? BrowseTable = null);

/// <summary>A saved snapshot of one connection's open tabs, most-recently-saved entries kept first in the store.</summary>
public sealed record WorkspaceEntry(string Connection, DateTimeOffset SavedAt, List<WorkspaceTab> Tabs, int ActiveTabIndex = 0);

/// <summary>Persists the last <see cref="MaxEntries"/> per-connection workspaces, most recent first.</summary>
public sealed class WorkspaceStore(string? filePath = null)
{
    private const int MaxEntries = 20;

    private readonly string _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "workspace.json");

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

    /// <summary>
    /// Replaces the saved workspace for <paramref name="connection"/> with the
    /// current tabs. Every tab's text, this connection's and the other
    /// connections' already in the file, goes through
    /// <see cref="SecretRedactor"/> on the way: the snapshot is written on every
    /// close and connection switch without the user asking, so an
    /// <c>ALTER ROLE … PASSWORD 'p'</c> left in a tab must not land on disk as
    /// typed (security audit 2026-09, finding 8). Doing it here rather than in
    /// the App's close handler is what makes it hold for every caller. The
    /// restored tab then shows the placeholder instead of the literal.
    /// </summary>
    public void Save(string connection, IReadOnlyList<WorkspaceTab> tabs, int activeTabIndex)
    {
        var entries = Load().Select(Redacted).ToList();
        entries.RemoveAll(e => string.Equals(e.Connection, connection, StringComparison.Ordinal));
        entries.Insert(0, new WorkspaceEntry(connection, DateTimeOffset.UtcNow, [.. tabs.Select(Redacted)], activeTabIndex));

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

    private static WorkspaceTab Redacted(WorkspaceTab tab) => tab with { Sql = SecretRedactor.Redact(tab.Sql) };

    private static WorkspaceEntry Redacted(WorkspaceEntry entry) => entry with { Tabs = [.. entry.Tabs.Select(Redacted)] };
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<WorkspaceEntry>))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
