using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Query;

/// <summary>Persists the last <see cref="MaxEntries"/> executions, most recent first, through <see cref="AppDataFile"/>.</summary>
public sealed class QueryHistoryStore(string? filePath = null)
{
    private const int MaxEntries = 200;

    private readonly string? _filePath = filePath ?? AppDataPaths.Resolve("history.json");

    /// <summary>The file this store reads and writes; null when the app has no data directory (then nothing is kept between sessions).</summary>
    public string? FilePath => _filePath;

    /// <summary>The saved history, or empty when there is no file, it cannot be read, or it cannot be parsed (then it is moved aside first).</summary>
    public IReadOnlyList<QueryHistoryEntry> Load() =>
        AppDataFile.ReadJson(_filePath, QueryHistoryJsonContext.Default.ListQueryHistoryEntry) ?? [];

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

    public void Save(IReadOnlyList<QueryHistoryEntry> entries) =>
        AppDataFile.WriteJson(_filePath, [.. entries], QueryHistoryJsonContext.Default.ListQueryHistoryEntry);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<QueryHistoryEntry>))]
internal sealed partial class QueryHistoryJsonContext : JsonSerializerContext;
