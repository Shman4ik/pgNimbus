using System.Text.Json;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Settings;

/// <summary>Persists <see cref="AppSettings"/> to a single JSON file under the app data root.</summary>
public sealed class AppSettingsStore
{
    private readonly string _filePath;

    public AppSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "settings.json");
    }

    /// <summary>
    /// Reads the saved settings, or returns defaults when there is no file yet.
    /// A missing/unreadable/corrupt file must never block startup, so any failure
    /// here falls back to defaults rather than throwing.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        File.WriteAllText(_filePath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
