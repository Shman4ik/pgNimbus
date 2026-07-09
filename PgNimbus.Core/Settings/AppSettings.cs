namespace PgNimbus.Core.Settings;

/// <summary>
/// Small bag of persisted, cross-session app preferences. A record with
/// defaulted <c>init</c> properties so a settings file written by an older
/// build — missing a field added later — still loads, with the new field
/// falling back to its default.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// The chosen theme: <c>"light"</c>, <c>"dark"</c>, or <c>"system"</c> (follow
    /// the OS). Kept as a plain string so <c>PgNimbus.Core</c> stays free of any
    /// UI-framework types; the App maps it to/from Avalonia's ThemeVariant.
    /// </summary>
    public string Theme { get; init; } = "system";

    /// <summary>
    /// Whether the schema sidebar shows advanced catalog objects (per-schema
    /// Functions groups and the root Extensions group) in addition to the
    /// default schemas/tables/roles view.
    /// </summary>
    public bool ShowAdvancedSchemaObjects { get; init; }
}
