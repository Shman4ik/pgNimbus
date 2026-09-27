namespace PgNimbus.Core.Text;

/// <summary>
/// Which relation an alias probably stands for when it is typed before the FROM
/// that will declare it — "columns first", <c>SELECT c.fi</c> ahead of
/// <c>FROM customers c</c> (second completion audit, E08). Nothing in the
/// statement says yet, so this reads the only evidence there is: how people
/// shorten a table's name.
/// </summary>
public static class AliasGuess
{
    /// <summary>
    /// How well <paramref name="alias"/> (folded, as the qualifier was typed)
    /// fits <paramref name="relation"/>, lower being better: 0 the name itself,
    /// 1 its initials — what the auto-alias writes (<c>oi</c> for
    /// <c>order_items</c>) — 2 the initials with a number (<c>o2</c>), 3 the
    /// start of the name (<c>inv</c> for <c>invoices</c>). Null when it fits
    /// none of these.
    /// </summary>
    public static int? Fit(string alias, string relation)
    {
        if (alias.Length == 0 || relation.Length == 0)
        {
            return null;
        }

        if (string.Equals(alias, relation, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var stem = TableAliaser.Initials(relation);
        if (string.Equals(alias, stem, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (alias.Length > stem.Length && alias.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && alias.AsSpan(stem.Length).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return 2;
        }

        return relation.StartsWith(alias, StringComparison.OrdinalIgnoreCase) ? 3 : null;
    }
}
