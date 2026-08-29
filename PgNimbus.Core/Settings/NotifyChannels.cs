namespace PgNimbus.Core.Settings;

/// <summary>
/// Reads and rewrites <see cref="AppSettings.NotifyChannels"/> — the
/// per-connection list of LISTEN channels the notification monitor subscribes
/// to. Pure and unit-tested, a sibling of <see cref="AutocompleteExclusions"/>:
/// the App only decides <em>when</em> a channel is added or dropped, never how
/// the persisted shape is spelled.
///
/// Channel names are compared ordinally, exactly as Postgres treats a quoted
/// identifier — <c>order_events</c> and <c>Order_Events</c> are two channels,
/// and a monitor that merged them would silently subscribe to the wrong one.
/// </summary>
public static class NotifyChannels
{
    /// <summary>
    /// The channels remembered for <paramref name="connectionKey"/> (the
    /// <c>host/database</c> key). Empty for an unknown key, and for a null one —
    /// an ad-hoc connection with no host has nothing to scope channels to.
    /// </summary>
    public static IReadOnlyList<string> For(AppSettings settings, string? connectionKey)
    {
        if (connectionKey is null || !settings.NotifyChannels.TryGetValue(connectionKey, out var names))
        {
            return [];
        }

        return names.ToList();
    }

    /// <summary>
    /// The whole map with <paramref name="connectionKey"/>'s entry replaced by
    /// <paramref name="channels"/> — deduped and sorted so settings.json stays
    /// stable across writes, and dropped outright when nothing is left, so
    /// removing the last channel leaves no empty husk behind. Returns a new
    /// dictionary; <paramref name="settings"/> is not mutated.
    /// </summary>
    public static Dictionary<string, List<string>> With(AppSettings settings, string connectionKey, IEnumerable<string> channels)
    {
        var map = new Dictionary<string, List<string>>(settings.NotifyChannels, StringComparer.Ordinal);
        var names = channels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

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
