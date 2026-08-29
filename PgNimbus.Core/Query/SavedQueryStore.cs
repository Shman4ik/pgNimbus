using System.Text.Json;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Query;

/// <summary>Persists user-named saved queries (no cap - the user manages these explicitly).</summary>
public sealed class SavedQueryStore(string? filePath = null)
{
    private readonly string _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "saved-queries.json");

    public IReadOnlyList<SavedQuery> Load()
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
            return JsonSerializer.Deserialize(json, SavedQueryJsonContext.Default.ListSavedQuery) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<SavedQuery> queries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize([.. queries], SavedQueryJsonContext.Default.ListSavedQuery);
        File.WriteAllText(_filePath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<SavedQuery>))]
internal sealed partial class SavedQueryJsonContext : JsonSerializerContext;
