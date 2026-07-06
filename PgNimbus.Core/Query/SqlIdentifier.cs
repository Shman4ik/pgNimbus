namespace PgNimbus.Core.Query;

/// <summary>Quotes a Postgres identifier (schema/table/column name) for safe interpolation into SQL text.</summary>
public static class SqlIdentifier
{
    public static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Quotes an identifier only when a bare (unquoted) form would change its
    /// meaning: Postgres folds unquoted identifiers to lowercase, so anything
    /// with an uppercase letter, a leading digit, a character outside
    /// <c>[a-z0-9_$]</c>, or a name that collides with a reserved keyword must be
    /// double-quoted to round-trip. A plain lowercase name like <c>users</c> is
    /// returned untouched, so completion insertions stay clean for the common case
    /// and only gain quotes (<c>"Spells"</c>) when they actually need them.
    /// </summary>
    public static string QuoteIfNeeded(string identifier) =>
        NeedsQuoting(identifier) ? Quote(identifier) : identifier;

    /// <summary>True when <paramref name="identifier"/> cannot be written bare without changing what it refers to.</summary>
    public static bool NeedsQuoting(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return true;
        }

        var first = identifier[0];
        if (!(char.IsAsciiLetterLower(first) || first == '_'))
        {
            return true;
        }

        foreach (var c in identifier)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '$'))
            {
                return true;
            }
        }

        return ReservedKeywords.Contains(identifier);
    }

    // Postgres reserved words (and reserved-but-usable-as-function/type words) that
    // are unsafe to use bare as a column/table name. Not exhaustive of every
    // keyword, but covers the ones that realistically show up as identifiers and
    // would otherwise be silently reinterpreted. Compared against the already-
    // lowercased bare form.
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc",
        "asymmetric", "authorization", "between", "binary", "both", "case",
        "cast", "check", "collate", "collation", "column", "concurrently",
        "constraint", "create", "cross", "current_catalog", "current_date",
        "current_role", "current_schema", "current_time", "current_timestamp",
        "current_user", "default", "deferrable", "desc", "distinct", "do",
        "else", "end", "except", "false", "fetch", "for", "foreign", "freeze",
        "from", "full", "grant", "group", "having", "ilike", "in", "initially",
        "inner", "intersect", "into", "is", "isnull", "join", "lateral",
        "leading", "left", "like", "limit", "localtime", "localtimestamp",
        "natural", "not", "notnull", "null", "offset", "on", "only", "or",
        "order", "outer", "overlaps", "placing", "primary", "references",
        "returning", "right", "select", "session_user", "similar", "some",
        "symmetric", "system_user", "table", "tablesample", "then", "to",
        "trailing", "true", "union", "unique", "user", "using", "variadic",
        "verbose", "when", "where", "window", "with",
    };
}
