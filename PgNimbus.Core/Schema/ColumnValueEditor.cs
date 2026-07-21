namespace PgNimbus.Core.Schema;

/// <summary>
/// Which input affordance a column's values call for, classified from the
/// column's *base* Postgres type — domains are resolved through
/// <c>pg_type.typbasetype</c> first, so a domain over an enum still gets the
/// enum dropdown. Anything without a dedicated editor (text, numerics, uuid,
/// ranges, …) stays <see cref="Text"/>: those already round-trip fine
/// through a plain text box.
/// </summary>
public enum ColumnValueEditor
{
    Text,

    /// <summary>boolean — a checkbox.</summary>
    Boolean,

    /// <summary>An enum type — a dropdown of its pg_enum labels.</summary>
    Enum,

    /// <summary>date — a calendar picker.</summary>
    Date,

    /// <summary>timestamp / timestamptz — a calendar picker plus a time-of-day field.</summary>
    Timestamp,

    /// <summary>Any array type — free text with client-side literal validation.</summary>
    Array,

    /// <summary>A composite (row) type — free text with client-side literal validation.</summary>
    Composite,

    /// <summary>json / jsonb — free text with client-side JSON validation, parsed and stored server-side via a cast to the declared type.</summary>
    Json,

    /// <summary>
    /// A type Postgres won't implicitly assign from text (it has no text→type
    /// assignment cast) but whose displayed value is already a valid input
    /// literal — network addresses, geometric types, ranges/multiranges, bit
    /// strings, xml, full-text search vectors/queries, bytea, and the like.
    /// Edited as free text (plain box, no dedicated widget) and parsed
    /// server-side via <c>CAST(@value AS declared-type)</c>, the same mechanism
    /// <see cref="Enum"/>/<see cref="Array"/>/<see cref="Composite"/>/<see cref="Json"/>
    /// use. No client-side syntax check — Postgres is the parser (the cast
    /// surfaces a precise error), since these literal grammars are too varied to
    /// pre-validate cheaply. Distinct from <see cref="Text"/>, which is for
    /// types that round-trip through a bare (uncast) text parameter or a CLR
    /// conversion (text/varchar, the numeric family, uuid, date/time).
    /// </summary>
    CastText,
}

public static class ColumnValueEditorClassifier
{
    /// <summary>
    /// Maps a base type's pg_type identity to an editor.
    /// <paramref name="typtype"/> and <paramref name="typcategory"/> are
    /// pg_type.typtype/typcategory of the *resolved* base type (never 'd' —
    /// domains are walked to their base before classification);
    /// <paramref name="typname"/> is its pg_type.typname.
    /// </summary>
    public static ColumnValueEditor Classify(char typtype, char typcategory, string typname) => typtype switch
    {
        'e' => ColumnValueEditor.Enum,
        'c' => ColumnValueEditor.Composite,
        _ when typcategory == 'A' => ColumnValueEditor.Array,
        _ => typname switch
        {
            "bool" => ColumnValueEditor.Boolean,
            "date" => ColumnValueEditor.Date,
            "timestamp" or "timestamptz" => ColumnValueEditor.Timestamp,
            // json/jsonb round-trip as text but need a server-side cast (there is
            // no implicit text→json[b] assignment cast) plus a JSON structure
            // check; jsonpath isn't JSON-shaped so it takes the plain cast path
            // (below), and hstore stays Text (its display needs an extension mapping).
            "json" or "jsonb" => ColumnValueEditor.Json,
            _ => NeedsServerCast(typcategory, typname) ? ColumnValueEditor.CastText : ColumnValueEditor.Text,
        },
    };

    /// <summary>
    /// Whether a base type must round-trip an inline edit through
    /// <c>CAST(text AS type)</c> rather than a bare text parameter — true for
    /// types Postgres won't implicitly assign from text (no text→type
    /// assignment cast), which otherwise fail with "column is of type X but
    /// expression is of type text". Whole pg_type categories qualify: network
    /// addresses ('I': inet/cidr), geometric types ('G'), ranges and
    /// multiranges ('R'), and bit strings ('V': bit/varbit) — so user-defined
    /// range types get the same treatment. A handful of category-'U' types share
    /// the property and are listed by name; uuid and json/jsonb are that
    /// category's round-trippable exceptions and are classified before this is
    /// reached. Numeric <c>money</c> deliberately stays <see cref="ColumnValueEditor.Text"/>:
    /// it round-trips through its CLR <c>decimal</c>.
    /// </summary>
    private static bool NeedsServerCast(char typcategory, string typname) =>
        typcategory is 'I' or 'G' or 'R' or 'V'
        || typname is "bytea" or "xml" or "tsvector" or "tsquery" or "jsonpath"
            or "macaddr" or "macaddr8" or "pg_lsn";
}
