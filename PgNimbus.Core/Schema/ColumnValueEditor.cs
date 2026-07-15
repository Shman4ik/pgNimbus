namespace PgNimbus.Core.Schema;

/// <summary>
/// Which input affordance a column's values call for, classified from the
/// column's *base* Postgres type — domains are resolved through
/// <c>pg_type.typbasetype</c> first, so a domain over an enum still gets the
/// enum dropdown. Anything without a dedicated editor (text, numerics, uuid,
/// json, ranges, …) stays <see cref="Text"/>: those already round-trip fine
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
            _ => ColumnValueEditor.Text,
        },
    };
}
