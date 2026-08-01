namespace PgNimbus.Core.Settings;

/// <summary>
/// Reads and rewrites <see cref="AppSettings.AutocompleteExcludedSchemas"/> —
/// the per-connection set of schemas the editor's completion ignores. Pure and
/// unit-tested, a read-only sibling of the other Core-side settings helpers: the
/// App only decides *when* a schema is excluded, never how the persisted shape
/// is spelled.
///
/// Names are compared ordinally, exactly as Postgres stores them, so a
/// <c>"Reporting"</c> schema and a <c>reporting</c> one stay distinct.
/// </summary>
public static class AutocompleteExclusions
{
    /// <summary>
    /// The schemas excluded for <paramref name="connectionKey"/> (the
    /// <c>host/database</c> key). Empty for an unknown key, and for a null one —
    /// an ad-hoc connection with no host has nothing to scope the exclusions to.
    /// </summary>
    public static IReadOnlySet<string> For(AppSettings settings, string? connectionKey)
    {
        if (connectionKey is null || !settings.AutocompleteExcludedSchemas.TryGetValue(connectionKey, out var names))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// The whole map with <paramref name="connectionKey"/>'s entry replaced by
    /// <paramref name="excluded"/> — deduped and sorted so settings.json stays
    /// stable across writes, and dropped outright when nothing is excluded, so
    /// clearing the last one leaves no empty husk behind. Returns a new
    /// dictionary; <paramref name="settings"/> is not mutated.
    /// </summary>
    public static Dictionary<string, List<string>> With(AppSettings settings, string connectionKey, IEnumerable<string> excluded)
    {
        var map = new Dictionary<string, List<string>>(settings.AutocompleteExcludedSchemas, StringComparer.Ordinal);
        var names = excluded.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        if (names.Count == 0)
        {
            map.Remove(connectionKey);
        }
        else
        {
            map[connectionKey] = names;
        }

        return map;
    }
}
