using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Query;

/// <summary>Persists user-named saved queries (no cap - the user manages these explicitly), through <see cref="AppDataFile"/>.</summary>
public sealed class SavedQueryStore(string? filePath = null)
{
    private readonly string? _filePath = filePath ?? AppDataPaths.Resolve("saved-queries.json");

    /// <summary>The file this store reads and writes; null when the app has no data directory (then nothing is kept between sessions).</summary>
    public string? FilePath => _filePath;

    /// <summary>The saved list, or empty when there is no file, it cannot be read, or it cannot be parsed (then it is moved aside first).</summary>
    public IReadOnlyList<SavedQuery> Load() =>
        AppDataFile.ReadJson(_filePath, SavedQueryJsonContext.Default.ListSavedQuery) ?? [];

    public void Save(IEnumerable<SavedQuery> queries) =>
        AppDataFile.WriteJson(_filePath, [.. queries], SavedQueryJsonContext.Default.ListSavedQuery);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<SavedQuery>))]
internal sealed partial class SavedQueryJsonContext : JsonSerializerContext;
