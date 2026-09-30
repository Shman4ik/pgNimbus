namespace PgNimbus.Core.Text;

/// <summary>One accepted completion row: how often, and when last.</summary>
public sealed record CompletionUsageEntry(string Id, int Count, DateTime LastUsed);

/// <summary>
/// What the user has accepted from the completion list on one connection, so
/// <see cref="CompletionRanker"/> can put the names they actually use first
/// (docs/design/sql-completion-audit-2.md B08, §6.2 step 4): more accepts
/// first, and among equals the more recent. Bounded — the least recently used
/// entry goes when the store is full — and persisted per connection by the
/// host (<see cref="Settings.CompletionUsageStore"/>). Ids are the rows'
/// stable identity (kind, detail, what they write), compared ordinally: two
/// schemas' <c>users</c> are two entries.
/// </summary>
public sealed class CompletionUsage
{
    /// <summary>How many rows one connection remembers.</summary>
    public const int Capacity = 1_000;

    private readonly Dictionary<string, CompletionUsageEntry> _entries = new(StringComparer.Ordinal);
    private Dictionary<string, int>? _ranks;

    public CompletionUsage()
    {
    }

    /// <summary>A store holding <paramref name="entries"/> (read back from disk).</summary>
    public CompletionUsage(IEnumerable<CompletionUsageEntry> entries)
    {
        foreach (var entry in entries.OrderByDescending(e => e.LastUsed).Take(Capacity))
        {
            _entries[entry.Id] = entry;
        }
    }

    /// <summary>Raised after every <see cref="Record"/>, for the host to persist.</summary>
    public event Action? Changed;

    /// <summary>Counts one accept of <paramref name="id"/> at <paramref name="now"/>.</summary>
    public void Record(string id, DateTime now)
    {
        var count = _entries.TryGetValue(id, out var existing) ? existing.Count + 1 : 1;
        if (existing is null && _entries.Count >= Capacity)
        {
            var oldest = _entries.Values.MinBy(e => e.LastUsed)!;
            _entries.Remove(oldest.Id);
        }

        _entries[id] = new CompletionUsageEntry(id, count, now);
        _ranks = null;
        Changed?.Invoke();
    }

    /// <summary>Counts one accept of <paramref name="id"/> now.</summary>
    public void Record(string id) => Record(id, DateTime.UtcNow);

    /// <summary>
    /// 0 for the most used row, 1 for the next, …: more accepts first, the
    /// more recent among equals. <see cref="int.MaxValue"/> for a row never accepted.
    /// </summary>
    public int RankOf(string id) =>
        (_ranks ??= BuildRanks()).TryGetValue(id, out var rank) ? rank : int.MaxValue;

    /// <summary>Everything remembered, for persisting.</summary>
    public IReadOnlyList<CompletionUsageEntry> Entries => [.. _entries.Values.OrderByDescending(e => e.LastUsed)];

    private Dictionary<string, int> BuildRanks()
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var rank = 0;
        foreach (var entry in _entries.Values.OrderByDescending(e => e.Count).ThenByDescending(e => e.LastUsed))
        {
            ranks[entry.Id] = rank++;
        }

        return ranks;
    }
}
