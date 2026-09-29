using System.Text.Json.Serialization;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Persists the saved connection list as JSON. Safe by construction: since
/// <see cref="ConnectionProfile"/> has no password property, there is
/// nothing sensitive for this store to ever write to disk. Reads and writes
/// go through <see cref="AppDataFile"/>: a file this store cannot parse is
/// moved aside rather than read as "no profiles" — the connection dialog
/// autosaves on every edit, and one torn file used to cost every profile.
/// </summary>
public sealed class ConnectionProfileStore(string? filePath = null)
{
    private readonly string? _filePath = filePath ?? AppDataPaths.Resolve("connections.json");

    public IReadOnlyList<ConnectionProfile> Load() =>
        AppDataFile.ReadJson(_filePath, ConnectionProfileJsonContext.Default.ListConnectionProfile) ?? [];

    public void Save(IEnumerable<ConnectionProfile> profiles) =>
        AppDataFile.WriteJson(_filePath, [.. profiles], ConnectionProfileJsonContext.Default.ListConnectionProfile);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ConnectionProfile>))]
internal sealed partial class ConnectionProfileJsonContext : JsonSerializerContext;
