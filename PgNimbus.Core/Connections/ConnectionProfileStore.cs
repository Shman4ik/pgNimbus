using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Persists the saved connection list as JSON. Safe by construction: since
/// <see cref="ConnectionProfile"/> has no password property, there is
/// nothing sensitive for this store to ever write to disk.
/// </summary>
public sealed class ConnectionProfileStore
{
    private readonly string _filePath;

    public ConnectionProfileStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "connections.json");
    }

    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        // A corrupt/empty/half-written file must never block the connection
        // dialog - fall back to an empty list rather than throwing at startup.
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, ConnectionProfileJsonContext.Default.ListConnectionProfile) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(profiles.ToList(), ConnectionProfileJsonContext.Default.ListConnectionProfile);
        File.WriteAllText(_filePath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ConnectionProfile>))]
internal sealed partial class ConnectionProfileJsonContext : JsonSerializerContext;
