namespace PgNimbus.Core.Text;

/// <summary>How well a candidate's name matches what was typed; lower is better.</summary>
public enum CompletionMatchTier
{
    /// <summary>The name is what was typed (<c>NULL</c> for "null").</summary>
    Exact,
    /// <summary>The name starts with it (<c>email</c> for "em").</summary>
    Prefix,
    /// <summary>It spells the starts of consecutive parts of the name (<c>order_items</c> for "oi", <c>error_message</c> for "em").</summary>
    PartStarts,
    /// <summary>The name contains it.</summary>
    Substring,
    /// <summary>Only a scattered subsequence matches.</summary>
    Fuzzy,
}

/// <summary>
/// Filters and orders SQL completion candidates against what the user has
/// typed so far, replacing the strict-prefix filter of the stock editor list.
/// A candidate is on the list when its name holds the typed letters in order
/// (<see cref="FuzzyMatcher"/>), and the list is ordered by, in turn
/// (docs/design/sql-completion-audit-2.md §6.2):
/// <list type="number">
/// <item>the match tier (<see cref="CompletionMatchTier"/>): an exact name,
/// then names starting with the text, then names whose parts start with it,
/// then names containing it, then the rest — so a real prefix is never beaten
/// by an abbreviation that happens to score higher (<c>em</c> → <c>email</c>,
/// not <c>error_message</c>);</item>
/// <item>the caller's context priority (the statement's own columns above the
/// catalog, legal keywords above illegal ones);</item>
/// <item>how often and how recently the user accepted the candidate;</item>
/// <item>the fuzzy score, then the shorter name, then the incoming order.</item>
/// </list>
/// With nothing typed the list is ordered by priority and use alone.
/// </summary>
public static class CompletionRanker
{
    /// <param name="Items">The matching candidates, best first.</param>
    /// <param name="SelectedIndex">The item to pre-select: the first, or -1 when there is none.</param>
    public readonly record struct Ranked<T>(IReadOnlyList<T> Items, int SelectedIndex);

    /// <summary>
    /// Ranks <paramref name="candidates"/> for the typed <paramref name="query"/>:
    /// drops non-matches and sorts the rest best-first.
    /// </summary>
    /// <param name="textOf">The label the user's input is matched against.</param>
    /// <param name="priorityOf">Context ranking hint — higher wins within a match tier.</param>
    /// <param name="usageOf">Usage rank per candidate, lower = used more (and more
    /// recently), <see cref="int.MaxValue"/> = never (see <see cref="CompletionUsage"/>).</param>
    public static Ranked<T> Rank<T>(
        IReadOnlyList<T> candidates,
        string query,
        Func<T, string> textOf,
        Func<T, double> priorityOf,
        Func<T, int> usageOf) =>
        Rank(candidates, query, textOf, priorityOf, usageOf, within: null, out _);

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
        Func<T, int> usageOf,
        IReadOnlyList<int>? within,
        out List<int> matched)
    {
        matched = [];
        if (query.Length == 0)
        {
            matched.AddRange(Enumerable.Range(0, candidates.Count));
            var ordered = OrderByPriority(candidates, priorityOf, usageOf);
            return new Ranked<T>(ordered, ordered.Count == 0 ? -1 : 0);
        }

        var matches = new List<Match<T>>();
        var count = within?.Count ?? candidates.Count;
        for (var n = 0; n < count; n++)
        {
            var i = within?[n] ?? n;
            var text = textOf(candidates[i]);
            if (FuzzyMatcher.Score(text, query) is { } score)
            {
                var item = candidates[i];
                matches.Add(new Match<T>(item, TierOf(text, query), priorityOf(item), usageOf(item), score, text.Length, i));
                matched.Add(i);
            }
        }

        matches.Sort(static (a, b) =>
        {
            if (a.Tier != b.Tier)
            {
                return a.Tier.CompareTo(b.Tier);
            }

            if (a.Priority != b.Priority)
            {
                return b.Priority.CompareTo(a.Priority);
            }

            if (a.Usage != b.Usage)
            {
                return a.Usage.CompareTo(b.Usage);
            }

            if (a.Score != b.Score)
            {
                return b.Score.CompareTo(a.Score);
            }

            if (a.Length != b.Length)
            {
                return a.Length.CompareTo(b.Length);
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

    private readonly record struct Match<T>(T Item, CompletionMatchTier Tier, double Priority, int Usage, int Score, int Length, int Index);

    /// <summary>How <paramref name="name"/> matches <paramref name="query"/> (which it must match as a subsequence).</summary>
    public static CompletionMatchTier TierOf(string name, string query)
    {
        if (name.Length == query.Length && name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return CompletionMatchTier.Exact;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return CompletionMatchTier.Prefix;
        }

        if (MatchesPartStarts(name, query))
        {
            return CompletionMatchTier.PartStarts;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase)
            ? CompletionMatchTier.Substring
            : CompletionMatchTier.Fuzzy;
    }

    // True when `query` is the concatenation of non-empty starts of
    // consecutive parts of `name` (parts split at _ . space - and at a
    // lower→upper or letter→digit step): "oi" and "ordit" for order_items.
    private static bool MatchesPartStarts(string name, string query)
    {
        Span<int> starts = stackalloc int[Math.Min(name.Length, 64)];
        var parts = 0;
        for (var i = 0; i < name.Length && parts < starts.Length; i++)
        {
            if (IsPartStart(name, i))
            {
                starts[parts++] = i;
            }
        }

        for (var p = 0; p < parts; p++)
        {
            if (MatchFrom(name, starts[..parts], p, query, 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPartStart(string name, int i)
    {
        var c = name[i];
        if (!char.IsLetterOrDigit(c))
        {
            return false;
        }

        if (i == 0)
        {
            return true;
        }

        var previous = name[i - 1];
        return !char.IsLetterOrDigit(previous)
            || (char.IsUpper(c) && char.IsLower(previous))
            || (char.IsDigit(c) && !char.IsDigit(previous));
    }

    private static bool MatchFrom(string name, ReadOnlySpan<int> starts, int part, string query, int q)
    {
        if (q == query.Length)
        {
            return true;
        }

        if (part >= starts.Length)
        {
            return false;
        }

        var end = part + 1 < starts.Length ? starts[part + 1] : name.Length;
        for (var length = 1; q + length <= query.Length && starts[part] + length <= end; length++)
        {
            if (char.ToLowerInvariant(name[starts[part] + length - 1]) != char.ToLowerInvariant(query[q + length - 1]))
            {
                break;
            }

            if (MatchFrom(name, starts, part + 1, query, q + length))
            {
                return true;
            }
        }

        return false;
    }

    private const int MaxUsageBucket = 5_000;

    // Nothing typed: by priority, then use, keeping the incoming order
    // otherwise. Bucketed rather than sorted: a catalog-wide list can hold a
    // million rows and only a few dozen distinct priorities.
    private static List<T> OrderByPriority<T>(IReadOnlyList<T> candidates, Func<T, double> priorityOf, Func<T, int> usageOf)
    {
        var buckets = new SortedDictionary<double, List<int>>(Comparer<double>.Create(static (a, b) => b.CompareTo(a)));
        for (var i = 0; i < candidates.Count; i++)
        {
            var priority = priorityOf(candidates[i]);
            if (!buckets.TryGetValue(priority, out var bucket))
            {
                buckets[priority] = bucket = [];
            }

            bucket.Add(i);
        }

        var result = new List<T>(candidates.Count);
        foreach (var bucket in buckets.Values)
        {
            // The used ones first within a priority, most used first; the rest
            // as they came. A bucket of catalog-wide rows (a hundred thousand
            // columns) keeps its order: that is not where a used name is found
            // with nothing typed, and asking each row costs a lookup.
            if (bucket.Count > MaxUsageBucket)
            {
                foreach (var i in bucket)
                {
                    result.Add(candidates[i]);
                }

                continue;
            }

            List<(int Usage, int Index)>? used = null;
            foreach (var i in bucket)
            {
                if (usageOf(candidates[i]) is var usage && usage != int.MaxValue)
                {
                    (used ??= []).Add((usage, i));
                }
            }

            if (used is not null)
            {
                used.Sort();
                foreach (var (_, i) in used)
                {
                    result.Add(candidates[i]);
                }
            }

            foreach (var i in bucket)
            {
                if (used is null || usageOf(candidates[i]) == int.MaxValue)
                {
                    result.Add(candidates[i]);
                }
            }
        }

        return result;
    }
}
