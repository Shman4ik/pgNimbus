namespace PgNimbus.Core.Text;

/// <summary>
/// Derives the short alias completion appends when a table is accepted after
/// FROM/JOIN: the initials of the name's words (<c>order_items</c> → <c>oi</c>,
/// <c>orders</c> → <c>o</c>), deduplicated against the names already taken in
/// the statement with a numeric suffix (<c>o</c>, <c>o2</c>, <c>o3</c>…).
/// </summary>
public static class TableAliaser
{
    /// <summary>
    /// The alias for <paramref name="table"/> (its bare, unquoted name), unique
    /// against <paramref name="taken"/> — the statement's existing aliases,
    /// table and CTE names, compared case-insensitively.
    /// </summary>
    public static string Derive(string table, IEnumerable<string> taken)
    {
        var takenSet = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var stem = Initials(table);

        // A reserved word can't stand as a bare alias ("FROM order_names on"
        // would misparse) — skip straight to the numbered form.
        var candidate = ReservedStems.Contains(stem) ? stem + "2" : stem;
        for (var n = 2; takenSet.Contains(candidate); n++)
        {
            candidate = stem + n;
        }

        return candidate;
    }

    // First letter of each word, splitting on underscores/digits/punctuation
    // and on lowercase→uppercase transitions ("OrderItems" → "oi"), lowercased.
    // Falls back to "t" when the name yields no letters (e.g. all digits).
    private static string Initials(string table)
    {
        Span<char> initials = stackalloc char[table.Length];
        var count = 0;
        var previous = '\0';
        foreach (var c in table)
        {
            if (char.IsLetter(c) && (!char.IsLetter(previous) || (char.IsUpper(c) && char.IsLower(previous))))
            {
                initials[count++] = char.ToLowerInvariant(c);
            }

            previous = c;
        }

        return count == 0 ? "t" : new string(initials[..count]);
    }

    // The short PostgreSQL reserved keywords an initials-derived alias could
    // realistically collide with. Longer reserved words (SELECT, BETWEEN…)
    // can't come out of Initials, so they're not listed.
    private static readonly HashSet<string> ReservedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "and", "any", "as", "asc", "both", "case", "cast", "desc", "do",
        "else", "end", "for", "from", "in", "into", "is", "not", "null", "on",
        "only", "or", "some", "then", "to", "true", "false", "user", "when",
        "with",
    };
}
