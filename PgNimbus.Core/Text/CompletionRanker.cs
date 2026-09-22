namespace PgNimbus.Core.Text;

/// <summary>
/// Filters and orders SQL completion candidates against what the user has
/// typed so far, replacing the strict-prefix filter of the stock editor list.
/// Matching is <see cref="FuzzyMatcher"/> subsequence matching, so "dr" finds
/// <c>daily_revenue</c> (word-boundary hit on the "r") and not just
/// <c>DROP</c>. Ties are broken by exact-prefix, then the caller-supplied
/// context priority (the statement's own columns / FK-neighbor tables float
/// above the flat catalog), then shorter name (so "ord" preselects
/// <c>orders</c> over <c>order_items</c>), then recency of use.
/// </summary>
public static class CompletionRanker
{
    /// <param name="Items">The matching candidates, best first.</param>
    /// <param name="SelectedIndex">The item to pre-select. Index 0 when the
    /// query is non-empty; the highest-priority item for an empty query, where
    /// the incoming order is preserved.</param>
    public readonly record struct Ranked<T>(IReadOnlyList<T> Items, int SelectedIndex);

    /// <summary>
    /// Ranks <paramref name="candidates"/> for the typed <paramref name="query"/>.
    /// An empty query keeps the incoming order (the provider already puts
    /// context items first) and only picks the pre-selection; a non-empty query
    /// drops non-matches and sorts the rest best-first.
    /// </summary>
    /// <param name="textOf">The label the user's input is matched against.</param>
    /// <param name="priorityOf">Context ranking hint — higher wins ties.</param>
    /// <param name="recencyOf">Recency rank per label, lower = more recently
    /// accepted, <see cref="int.MaxValue"/> = never (see <see cref="CompletionRecency"/>).</param>
    public static Ranked<T> Rank<T>(
        IReadOnlyList<T> candidates,
        string query,
        Func<T, string> textOf,
        Func<T, double> priorityOf,
        Func<T, int> recencyOf) =>
        Rank(candidates, query, textOf, priorityOf, recencyOf, within: null, out _);

    /// <summary>
    /// <see cref="Rank{T}(IReadOnlyList{T}, string, Func{T, string}, Func{T, double}, Func{T, int})"/>
    /// over only the candidates at <paramref name="within"/> (ascending
    /// indexes into <paramref name="candidates"/>; null = all of them), also
    /// handing back the indexes that matched. Typing one more character can
    /// only lose matches — a subsequence of the longer query is a subsequence
    /// of the shorter — so the popup passes the previous keystroke's matches
    /// and a keystroke over a hundred-thousand-row catalog scores only the
    /// rows still in play. The result is the same as ranking everything: ties
    /// still fall back to the original index.
    /// </summary>
    public static Ranked<T> Rank<T>(
        IReadOnlyList<T> candidates,
        string query,
        Func<T, string> textOf,
        Func<T, double> priorityOf,
        Func<T, int> recencyOf,
        IReadOnlyList<int>? within,
        out List<int> matched)
    {
        matched = [];
        if (query.Length == 0)
        {
            var selected = 0;
            var bestPriority = double.NegativeInfinity;
            for (var i = 0; i < candidates.Count; i++)
            {
                var priority = priorityOf(candidates[i]);
                if (priority > bestPriority)
                {
                    bestPriority = priority;
                    selected = i;
                }
            }

            matched.AddRange(Enumerable.Range(0, candidates.Count));
            return new Ranked<T>(candidates, candidates.Count == 0 ? -1 : selected);
        }

        var matches = new List<(T Item, int Score, bool ExactPrefix, int Index)>();
        var count = within?.Count ?? candidates.Count;
        for (var n = 0; n < count; n++)
        {
            var i = within?[n] ?? n;
            var text = textOf(candidates[i]);
            if (FuzzyMatcher.Score(text, query) is { } score)
            {
                matches.Add((candidates[i], score, text.StartsWith(query, StringComparison.OrdinalIgnoreCase), i));
                matched.Add(i);
            }
        }

        matches.Sort((a, b) =>
        {
            if (a.Score != b.Score)
            {
                return b.Score.CompareTo(a.Score);
            }

            if (a.ExactPrefix != b.ExactPrefix)
            {
                return a.ExactPrefix ? -1 : 1;
            }

            var byPriority = priorityOf(b.Item).CompareTo(priorityOf(a.Item));
            if (byPriority != 0)
            {
                return byPriority;
            }

            var byLength = textOf(a.Item).Length.CompareTo(textOf(b.Item).Length);
            if (byLength != 0)
            {
                return byLength;
            }

            var byRecency = recencyOf(a.Item).CompareTo(recencyOf(b.Item));
            if (byRecency != 0)
            {
                return byRecency;
            }

            return a.Index.CompareTo(b.Index); // stable
        });

        var items = new List<T>(matches.Count);
        foreach (var match in matches)
        {
            items.Add(match.Item);
        }

        return new Ranked<T>(items, items.Count == 0 ? -1 : 0);
    }
}
