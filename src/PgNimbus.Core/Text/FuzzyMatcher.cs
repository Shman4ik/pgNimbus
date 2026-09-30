namespace PgNimbus.Core.Text;

/// <summary>
/// Lightweight subsequence fuzzy matcher shared by the command palette and the
/// SQL completion popup (via <see cref="CompletionRanker"/>). Returns a
/// score (higher is better) when every character of the query appears in order
/// in the target, or <c>null</c> when it doesn't match at all. Rewards a prefix
/// hit, word-boundary hits, and consecutive runs so that, e.g., typing
/// "cust" ranks "customers" above "created_at_customs".
/// </summary>
public static class FuzzyMatcher
{
    public static int? Score(string target, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (query.Length > target.Length)
        {
            return null;
        }

        var score = 0;
        var targetIndex = 0;
        var consecutive = 0;

        foreach (var qc in query)
        {
            var lowerQuery = char.ToLowerInvariant(qc);
            var matched = false;

            while (targetIndex < target.Length)
            {
                var tc = target[targetIndex];
                if (char.ToLowerInvariant(tc) == lowerQuery)
                {
                    var bonus = 1;
                    if (targetIndex == 0)
                    {
                        bonus += 8; // prefix match — the strongest signal
                    }
                    else if (!char.IsLetterOrDigit(target[targetIndex - 1]))
                    {
                        bonus += 4; // start of a word (after '.', '_', space, ...)
                    }

                    bonus += consecutive * 2; // adjacency run
                    score += bonus;
                    consecutive++;
                    targetIndex++;
                    matched = true;
                    break;
                }

                consecutive = 0;
                targetIndex++;
            }

            if (!matched)
            {
                return null;
            }
        }

        return score;
    }
}
