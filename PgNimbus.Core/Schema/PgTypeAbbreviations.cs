namespace PgNimbus.Core.Schema;

/// <summary>
/// Shortens the verbose, multi-word type names <c>format_type</c> returns (what
/// <see cref="SchemaService"/> reads) to their common Postgres aliases —
/// <c>timestamp with time zone</c> → <c>timestamptz</c>,
/// <c>character varying(255)</c> → <c>varchar(255)</c> — for a compact schema
/// tree, while the full name stays available for a tooltip.
/// </summary>
public static class PgTypeAbbreviations
{
    // Only the genuinely verbose base names; already-short ones (integer, text,
    // jsonb, numeric, …) are left exactly as they are.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["timestamp with time zone"] = "timestamptz",
        ["timestamp without time zone"] = "timestamp",
        ["time with time zone"] = "timetz",
        ["time without time zone"] = "time",
        ["character varying"] = "varchar",
        ["character"] = "char",
        ["bit varying"] = "varbit",
        ["double precision"] = "float8",
    };

    /// <summary>
    /// Returns the abbreviated form of <paramref name="type"/>, preserving any
    /// length/precision modifier and array marker (so <c>character varying(255)[]</c>
    /// becomes <c>varchar(255)[]</c>). Types with no known abbreviation come back
    /// unchanged.
    /// </summary>
    public static string Abbreviate(string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return type;
        }

        // Split the base name from a trailing "(modifier)" and/or "[]" suffix.
        var cut = type.AsSpan().IndexOfAny('(', '[');
        var baseName = (cut < 0 ? type : type[..cut]).TrimEnd();
        var suffix = cut < 0 ? "" : type[cut..];

        return Aliases.TryGetValue(baseName, out var alias) ? alias + suffix : type;
    }
}
