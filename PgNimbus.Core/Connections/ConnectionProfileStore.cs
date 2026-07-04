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
        _filePath = filePath ?? Path.Combine(ResolveAppDataDirectory(), "pgNimbus", "connections.json");
    }

    /// <summary>
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> can resolve to an
    /// empty string in minimal/containerized Linux environments (e.g. no usable
    /// passwd entry for the current UID), which would otherwise make Save/Load
    /// silently use a path relative to the working directory. Fall back to
    /// $HOME, then the OS temp directory, rather than risk that.
    /// </summary>
    private static string ResolveAppDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            return appData;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : Path.Combine(home, ".config");
    }

    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize(json, ConnectionProfileJsonContext.Default.ListConnectionProfile) ?? [];
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
