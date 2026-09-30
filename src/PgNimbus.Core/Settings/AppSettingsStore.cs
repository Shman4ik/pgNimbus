using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Settings;

/// <summary>Persists <see cref="AppSettings"/> to a single JSON file under the app data root, through <see cref="AppDataFile"/>.</summary>
public sealed class AppSettingsStore(string? filePath = null)
{
    private readonly string? _filePath = filePath ?? AppDataPaths.Resolve("settings.json");

    /// <summary>
    /// Reads the saved settings, or returns defaults when there is no file yet.
    /// A missing/unreadable/corrupt file must never block startup, so any failure
    /// here falls back to defaults rather than throwing (a corrupt file is moved
    /// aside first).
    /// </summary>
    public AppSettings Load() =>
        AppDataFile.ReadJson(_filePath, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();

    public void Save(AppSettings settings) =>
        AppDataFile.WriteJson(_filePath, settings, AppSettingsJsonContext.Default.AppSettings);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
