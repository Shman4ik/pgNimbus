using System.Text.Json;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Settings;

/// <summary>
/// Persists each connection's completion usage (<see cref="CompletionUsage"/>)
/// in <c>completion-usage.json</c>, keyed <c>host/database</c> like the
/// workspace snapshot and the autocomplete exclusions: which names a person
/// picks is a property of one database. Its own file rather than a field of
/// settings.json, which is rewritten by every preference toggle and would carry
/// up to a thousand rows per connection along. The last <see cref="MaxConnections"/>
/// connections are kept. Serialized through a source-generated context, as
/// NativeAOT requires.
/// </summary>
public sealed class CompletionUsageStore(string? filePath = null)
{
    private const int MaxConnections = 20;

    private readonly string _filePath = filePath ?? Path.Combine(AppDataPaths.GetRootDirectory(), "completion-usage.json");

    /// <summary>The usage remembered for <paramref name="connection"/>; empty for one never seen, or for a file that can't be read.</summary>
    public IReadOnlyList<CompletionUsageEntry> Load(string connection) =>
        LoadAll().FirstOrDefault(c => string.Equals(c.Connection, connection, StringComparison.Ordinal))?.Entries ?? [];

    /// <summary>Replaces <paramref name="connection"/>'s usage with <paramref name="entries"/>, moving it to the front.</summary>
    public void Save(string connection, IReadOnlyList<CompletionUsageEntry> entries)
    {
        var all = LoadAll().ToList();
        all.RemoveAll(c => string.Equals(c.Connection, connection, StringComparison.Ordinal));
        all.Insert(0, new ConnectionUsage(connection, [.. entries]));
        if (all.Count > MaxConnections)
        {
            all.RemoveRange(MaxConnections, all.Count - MaxConnections);
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(all, CompletionUsageJsonContext.Default.ListConnectionUsage));
    }

    private List<ConnectionUsage> LoadAll()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        // A corrupt or half-written file costs the ranking its memory, never startup.
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(_filePath), CompletionUsageJsonContext.Default.ListConnectionUsage) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>One connection's remembered accepts.</summary>
public sealed record ConnectionUsage(string Connection, List<CompletionUsageEntry> Entries);

[JsonSerializable(typeof(List<ConnectionUsage>))]
internal sealed partial class CompletionUsageJsonContext : JsonSerializerContext;
