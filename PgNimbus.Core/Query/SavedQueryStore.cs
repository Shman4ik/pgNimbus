using System.Text.Json;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Query;

/// <summary>Persists user-named saved queries (no cap - the user manages these explicitly).</summary>
public sealed class SavedQueryStore
{
    private readonly string _filePath;

    public SavedQueryStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "saved-queries.json");
    }

    public IReadOnlyList<SavedQuery> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize(json, SavedQueryJsonContext.Default.ListSavedQuery) ?? [];
    }

    public void Save(IEnumerable<SavedQuery> queries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(queries.ToList(), SavedQueryJsonContext.Default.ListSavedQuery);
        File.WriteAllText(_filePath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<SavedQuery>))]
internal sealed partial class SavedQueryJsonContext : JsonSerializerContext;
