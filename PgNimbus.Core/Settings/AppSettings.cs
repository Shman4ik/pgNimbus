namespace PgNimbus.Core.Settings;

/// <summary>
/// Small bag of persisted, cross-session app preferences. A record with
/// defaulted properties so a settings file written by an older build — missing
/// a field added later — still loads, with the new field falling back to its
/// default. The properties are <c>set</c>, not <c>init</c>, and that is
/// load-bearing: the source-generated JSON deserializer bypasses property
/// initializers for init-only setters, so an <c>init</c> flag defaulting to
/// true would silently read false from any settings file predating it.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// The chosen theme: <c>"light"</c>, <c>"dark"</c>, or <c>"system"</c> (follow
    /// the OS). Kept as a plain string so <c>PgNimbus.Core</c> stays free of any
    /// UI-framework types; the App maps it to/from Avalonia's ThemeVariant.
    /// </summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Whether the schema sidebar shows advanced catalog objects (per-schema
    /// Functions groups and the root Extensions group) in addition to the
    /// default schemas/tables/roles view.
    /// </summary>
    public bool ShowAdvancedSchemaObjects { get; set; }

    /// <summary>
    /// Whether accepting a table from completion after FROM/JOIN also appends a
    /// short alias (<c>public.orders</c> → <c>public.orders o</c>), so the
    /// <c>o.</c> member-access flow is available immediately. Off by default;
    /// opt in from the completion preferences toggle.
    /// </summary>
    public bool AutoAliasTables { get; set; }

    /// <summary>
    /// Which modifier the app's command shortcuts use: <c>"auto"</c> (Cmd on
    /// macOS, Ctrl elsewhere — the default), <c>"windows"</c> (always Ctrl), or
    /// <c>"mac"</c> (always Cmd). A plain string for the same reason as
    /// <see cref="Theme"/>; the App maps it to key modifiers.
    /// </summary>
    public string HotkeyScheme { get; set; } = "auto";
}
