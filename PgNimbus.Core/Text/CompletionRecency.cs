namespace PgNimbus.Core.Text;

/// <summary>
/// Remembers the most recently accepted completion labels so
/// <see cref="CompletionRanker"/> can use "picked it a moment ago" as a
/// tie-breaker. Bounded, most-recent-first, case-insensitive; per-session only
/// (deliberately not persisted — stale habits shouldn't outlive the window).
/// </summary>
public sealed class CompletionRecency
{
    private const int Capacity = 50;

    private readonly List<string> _recent = [];

    /// <summary>Marks <paramref name="text"/> as just accepted, moving it to the front.</summary>
    public void Record(string text)
    {
        var existing = RankOf(text);
        if (existing != int.MaxValue)
        {
            _recent.RemoveAt(existing);
        }
        else if (_recent.Count == Capacity)
        {
            _recent.RemoveAt(Capacity - 1);
        }

        _recent.Insert(0, text);
    }

    /// <summary>0 for the most recent accept, 1 for the one before it, …;
    /// <see cref="int.MaxValue"/> when never accepted (or already evicted).</summary>
    public int RankOf(string text)
    {
        for (var i = 0; i < _recent.Count; i++)
        {
            if (string.Equals(_recent[i], text, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
