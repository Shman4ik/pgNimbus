using System.Text.Json;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Query;

/// <summary>Persists the last <see cref="MaxEntries"/> executions, most recent first.</summary>
public sealed class QueryHistoryStore
{
    private const int MaxEntries = 200;

    private readonly string _filePath;

    public QueryHistoryStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "history.json");
    }

    public IReadOnlyList<QueryHistoryEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize(json, QueryHistoryJsonContext.Default.ListQueryHistoryEntry) ?? [];
    }

    public void Append(QueryHistoryEntry entry)
    {
        var entries = Load().ToList();
        entries.Insert(0, entry);

        // Trim oldest-first, but never a pinned entry - pinning is the
        // "keep this around" signal, so the cap only evicts unpinned ones.
        for (var i = entries.Count - 1; i >= 0 && entries.Count > MaxEntries; i--)
        {
            if (!entries[i].Pinned)
            {
                entries.RemoveAt(i);
            }
        }

        Save(entries);
    }

    public void Clear() => Save([]);

    public void Save(IReadOnlyList<QueryHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entries.ToList(), QueryHistoryJsonContext.Default.ListQueryHistoryEntry);
        File.WriteAllText(_filePath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<QueryHistoryEntry>))]
internal sealed partial class QueryHistoryJsonContext : JsonSerializerContext;
