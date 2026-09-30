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
    /// Whether the schema sidebar shows each relation's on-disk size as a dim
    /// hint next to its name. Off by default — it's an occasional need, not
    /// something worth cluttering every row with all the time.
    /// </summary>
    public bool ShowSchemaSizes { get; set; }

    /// <summary>
    /// Whether browsing a table always shows the filter-chip line, even with no
    /// condition in it. Off by default: the line appears on its own whenever a
    /// condition filters the rows, which is when it has something to say.
    /// </summary>
    public bool ShowFilterBar { get; set; }

    /// <summary>
    /// Whether accepting a table from completion after FROM/JOIN also appends a
    /// short alias (<c>public.orders</c> → <c>public.orders o</c>), so the
    /// <c>o.</c> member-access flow is available immediately. On by default;
    /// opt out from the completion preferences toggle.
    /// </summary>
    public bool AutoAliasTables { get; set; } = true;

    /// <summary>
    /// Safe mode: grid cell edits, row deletes, and Add-row inserts are staged
    /// locally (dirty rows highlighted in the grid) instead of executing
    /// immediately; the generated SQL is reviewed and committed as one
    /// transaction — or discarded. On by default; toggled from the command
    /// palette or the preferences page.
    /// </summary>
    public bool SafeModeEdits { get; set; } = true;

    /// <summary>
    /// Which modifier the app's command shortcuts use: <c>"auto"</c> (Cmd on
    /// macOS, Ctrl elsewhere — the default), <c>"windows"</c> (always Ctrl), or
    /// <c>"mac"</c> (always Cmd). A plain string for the same reason as
    /// <see cref="Theme"/>; the App maps it to key modifiers.
    /// </summary>
    public string HotkeyScheme { get; set; } = "auto";

    /// <summary>
    /// Notepad++-style word wrap in the SQL editor: long lines wrap to the pane
    /// width instead of scrolling horizontally. Off by default (the classic
    /// no-wrap editor); toggled from the editor command bar or the command
    /// palette, persisted here so the choice survives a restart.
    /// </summary>
    public bool WordWrapEditor { get; set; }

    /// <summary>
    /// The plan pane's view the next plan opens in: false is the text layout
    /// (the default, the classic <c>EXPLAIN</c> reading), true the heat-mapped
    /// tree. Whatever the user last picked with the Text/Tree switch, so a
    /// tree reader isn't sent back to text by every new plan.
    /// </summary>
    public bool PlanTreeView { get; set; }

    /// <summary>
    /// CSV export and the grid's TSV/CSV copies put a <c>'</c> in front of a
    /// text cell starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or
    /// carriage return, so a spreadsheet shows it instead of running it as a
    /// formula (security audit 2026-09, finding 18). Off by default: the quote
    /// changes the data for every reader that isn't a spreadsheet. Toggled from
    /// the command bar's Export menu.
    /// </summary>
    public bool SpreadsheetSafeExport { get; set; }

    /// <summary>
    /// The letter case completion writes keywords in: <c>"typed"</c> (the case
    /// being typed — <c>tr</c> gives <c>true</c>, the default), <c>"upper"</c>
    /// or <c>"lower"</c> (docs/dev/design/sql-completion-audit-2.md F02, §6.7).
    /// </summary>
    public string CompletionKeywordCase { get; set; } = "typed";

    /// <summary>
    /// Completion writes a table's schema always, rather than only when the
    /// bare name wouldn't find that table along the search_path (F01, §6.7).
    /// </summary>
    public bool CompletionAlwaysQualifyTables { get; set; }

    /// <summary>
    /// Enter accepts a completion row by the rule of §6.1 (true, the default);
    /// false leaves accepting to Tab alone and Enter always a newline.
    /// </summary>
    public bool CompletionEnterAccepts { get; set; } = true;

    /// <summary>
    /// The id of the connection profile that was last connected to, as a
    /// string (Core keeps the settings record free of any type the JSON
    /// source generator needs special handling for; the App parses it back to
    /// a <c>Guid</c>). The connection dialog preselects that profile so the
    /// common case — reconnect to what you used last — is Enter, not a hunt
    /// through the list. Null on a fresh install, or after the profile it
    /// pointed at was deleted.
    /// </summary>
    public string? LastConnectionProfileId { get; set; }

    /// <summary>
    /// Whether startup connects straight to <see cref="LastConnectionProfileId"/>
    /// instead of waiting on the connection dialog. Off by default — silently
    /// opening a connection is a surprise unless you asked for it. The dialog
    /// still shows while the connect is in flight, so a failure lands back in
    /// it with the error, and "Switch connection" remains the way to reach it
    /// deliberately.
    /// </summary>
    public bool AutoConnectLastProfile { get; set; }

    /// <summary>
    /// Whether every statement run is filed in the query history
    /// (<c>history.json</c>). On by default. The file keeps each statement's
    /// text as run, values included, unencrypted; passwords are masked by
    /// <see cref="Security.SecretRedactor"/>, but a query that selects by an
    /// email address keeps the address. Off, nothing new is recorded; what is
    /// already there stays until Clear History. Security audit 2026-09,
    /// finding 8: there used to be no way to turn it off.
    /// </summary>
    public bool RecordQueryHistory { get; set; } = true;

    /// <summary>
    /// Schemas kept out of editor autocomplete, keyed by connection
    /// (<c>host/database</c>, the same key the workspace snapshot uses). A big
    /// database routinely carries schemas another team owns; excluding them
    /// drops their tables, columns and functions from every completion list —
    /// and skips their catalog queries on refresh — without hiding them from
    /// the schema tree. Names match ordinally, exactly as Postgres stores them.
    /// Read and rewritten through <see cref="AutocompleteExclusions"/>.
    /// </summary>
    public Dictionary<string, List<string>> AutocompleteExcludedSchemas { get; set; } = [];

    /// <summary>
    /// The LISTEN channels the notification monitor subscribes to, keyed by
    /// connection (<c>host/database</c>, the same key the workspace snapshot
    /// uses). Channels belong to one application's event plumbing, not to the
    /// app, and retyping them after every restart was most of the reason the
    /// monitor went unused. Read and rewritten through
    /// <see cref="NotifyChannels"/>.
    /// </summary>
    public Dictionary<string, List<string>> NotifyChannels { get; set; } = [];

    /// <summary>
    /// The most recently opened/saved <c>.sql</c> file paths, most recent
    /// first, capped at 10 by the caller. Backs the command palette's
    /// "Recent file" entries. <c>set</c>, not <c>init</c>, for the same
    /// reason as every other property here — an <c>init</c> collection would
    /// silently come back <c>null</c> (not the empty-list default) from a
    /// settings file predating this field, since the source-generated
    /// deserializer bypasses property initializers for init-only setters.
    /// </summary>
    public List<string> RecentSqlFiles { get; set; } = [];
}
