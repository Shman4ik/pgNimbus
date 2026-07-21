namespace PgNimbus.Core.Schema;

/// <summary>
/// A broad visual family a Postgres type belongs to, used to pick a single
/// icon/accent for a column in the schema tree and results grid. This is a
/// coarser bucket than the exact type — every integer/numeric/float shares the
/// <see cref="Numeric"/> family, every char/text/varchar shares <see cref="Text"/>,
/// and so on — so the UI speaks one consistent visual language regardless of the
/// dozens of concrete type names Postgres reports.
/// </summary>
public enum PgTypeCategory
{
    /// <summary>Anything without a more specific family (xml, ltree, unknown user types, …).</summary>
    Other,

    /// <summary>smallint/integer/bigint, numeric/decimal, real/double, money, oid.</summary>
    Numeric,

    /// <summary>text, varchar, char, name, citext.</summary>
    Text,

    /// <summary>boolean.</summary>
    Boolean,

    /// <summary>date, time, timestamp (with/without tz), interval.</summary>
    DateTime,

    /// <summary>uuid.</summary>
    Uuid,

    /// <summary>json, jsonb, jsonpath, hstore.</summary>
    Json,

    /// <summary>inet, cidr, macaddr, macaddr8.</summary>
    Network,

    /// <summary>point, line, lseg, box, path, polygon, circle.</summary>
    Geometric,

    /// <summary>int4range/numrange/tstzrange/… and their multirange counterparts.</summary>
    Range,

    /// <summary>bytea.</summary>
    Binary,

    /// <summary>bit, bit varying (varbit).</summary>
    BitString,

    /// <summary>pgvector: vector, halfvec, sparsevec.</summary>
    Vector,

    /// <summary>tsvector, tsquery.</summary>
    FullText,

    /// <summary>Any array type (integer[], text[], …).</summary>
    Array,

    /// <summary>An enum type — one of a fixed set of labels. Not detectable from a bare name; needs the pg_type kind.</summary>
    Enum,

    /// <summary>A composite (row) type — a record of named fields. Not detectable from a bare name.</summary>
    Composite,
}

/// <summary>
/// Classifies a Postgres type <em>name</em> into a coarse <see cref="PgTypeCategory"/>.
/// Deliberately name-based so the same helper works for both the
/// <c>format_type</c> strings <see cref="SchemaService"/> reads for the schema
/// tree and the wire-protocol <c>GetDataTypeName</c> strings a result set carries
/// per column — which are nearly identical spellings. Enum/composite/domain can't
/// be told apart from a bare name, so those land in a base-type or
/// <see cref="PgTypeCategory.Other"/> family; callers that already know the
/// pg_type kind (e.g. the schema tree, via <see cref="ColumnValueEditor"/>) can
/// refine from there.
/// </summary>
public static class PgTypeCategorizer
{
    public static PgTypeCategory Categorize(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return PgTypeCategory.Other;
        }

        var name = Normalize(typeName, out var isArray);
        if (isArray)
        {
            return PgTypeCategory.Array;
        }

        // Ranges/multiranges: match by suffix so user-defined ranges over a
        // base type (myrange) are missed (they fall through to Other), but every
        // built-in *range/*multirange is caught without listing each one.
        if (name.EndsWith("multirange", StringComparison.Ordinal) ||
            name.EndsWith("range", StringComparison.Ordinal) && IsKnownRange(name))
        {
            return PgTypeCategory.Range;
        }

        return name switch
        {
            "smallint" or "integer" or "bigint" or "int" or "int2" or "int4" or "int8"
                or "numeric" or "decimal" or "real" or "double precision" or "float4" or "float8"
                or "money" or "oid" => PgTypeCategory.Numeric,

            "text" or "character varying" or "varchar" or "character" or "char" or "bpchar"
                or "name" or "citext" or "\"char\"" or "xml" => PgTypeCategory.Text,

            "boolean" or "bool" => PgTypeCategory.Boolean,

            "date" or "timestamp" or "timestamptz" or "timestamp without time zone"
                or "timestamp with time zone" or "time" or "timetz" or "time without time zone"
                or "time with time zone" or "interval" => PgTypeCategory.DateTime,

            "uuid" => PgTypeCategory.Uuid,

            "json" or "jsonb" or "jsonpath" or "hstore" => PgTypeCategory.Json,

            "inet" or "cidr" or "macaddr" or "macaddr8" => PgTypeCategory.Network,

            "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle" => PgTypeCategory.Geometric,

            "bytea" => PgTypeCategory.Binary,

            "bit" or "bit varying" or "varbit" => PgTypeCategory.BitString,

            "vector" or "halfvec" or "sparsevec" => PgTypeCategory.Vector,

            "tsvector" or "tsquery" => PgTypeCategory.FullText,

            _ => PgTypeCategory.Other,
        };
    }

    /// <summary>
    /// Classifies a column using both its type name and the pg_type kind the
    /// catalog already resolved into a <see cref="ColumnValueEditor"/>. Enum and
    /// composite types have no category of their own by name (they'd be
    /// <see cref="PgTypeCategory.Other"/>); the editor is the one signal that tells
    /// them apart, so it wins. Everything else falls through to the name-based
    /// <see cref="Categorize(string?)"/> with domains resolved to their base type.
    /// </summary>
    public static PgTypeCategory CategorizeColumn(string? declaredType, string? domainBaseType, ColumnValueEditor editor) => editor switch
    {
        ColumnValueEditor.Enum => PgTypeCategory.Enum,
        ColumnValueEditor.Composite => PgTypeCategory.Composite,
        _ => Categorize(ClassifierType(declaredType, domainBaseType)),
    };

    /// <summary>
    /// The type name a possibly-domain column should be classified by: the
    /// domain's resolved base type when the declared type has no category of its
    /// own (a user domain name is <see cref="PgTypeCategory.Other"/> by name), so
    /// a domain over citext still reads as Text and a domain over inet as Network.
    /// Returns <paramref name="declaredType"/> unchanged when it already classifies
    /// or there's no base type to fall back to.
    /// </summary>
    public static string? ClassifierType(string? declaredType, string? domainBaseType) =>
        !string.IsNullOrWhiteSpace(domainBaseType) && Categorize(declaredType) == PgTypeCategory.Other
            ? domainBaseType
            : declaredType;

    // The only ranges we treat as ranges by name — user ranges (typtype 'r' with
    // a bespoke name) can't be recognized from the string and stay Other.
    private static bool IsKnownRange(string name) => name is
        "int4range" or "int8range" or "numrange" or "tsrange" or "tstzrange" or "daterange";

    /// <summary>
    /// Lower-cases, drops a length/precision modifier ("numeric(10,2)" → "numeric"),
    /// strips schema qualification ("public.mood" → "mood"), and reports whether the
    /// type is an array (trailing "[]" from format_type, or a leading "_" internal
    /// array name) — with the array marker removed from the returned base name.
    /// </summary>
    private static string Normalize(string typeName, out bool isArray)
    {
        var span = typeName.AsSpan().Trim();

        // Cut any "(modifier)" — always the tail of the base-type spelling.
        var paren = span.IndexOf('(');
        if (paren >= 0)
        {
            // Keep a possible "[]" that follows the modifier (e.g. "numeric(10,2)[]").
            var afterParen = span[paren..];
            var closer = afterParen.IndexOf(')');
            var suffix = closer >= 0 ? afterParen[(closer + 1)..] : default;
            span = string.Concat(span[..paren].ToString(), suffix.ToString()).AsSpan();
        }

        span = span.Trim();

        isArray = false;
        if (span.EndsWith("[]"))
        {
            isArray = true;
            span = span[..^2].TrimEnd();
        }

        // Schema-qualified user types ("public.mood") — keep the local name.
        var dot = span.LastIndexOf('.');
        if (dot >= 0)
        {
            span = span[(dot + 1)..];
        }

        var name = span.ToString().ToLowerInvariant();

        // Internal array spelling ("_int4") — only when not already handled above.
        if (!isArray && name.Length > 1 && name[0] == '_')
        {
            isArray = true;
        }

        return name;
    }
}
