using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using PgNimbus.Core.Localization;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Proves no interface text is left out of <c>Strings.resx</c>: every screenshot
/// scenario is built in the pseudo-locale, where every string from the table reads
/// <c>[Àççéñţéð]</c>, and any visible text with letters that carries no such
/// bracket was written somewhere else: in XAML, a view model or code-behind. Data
/// the fixtures show (names, values, SQL) is told apart by where it is drawn
/// (<see cref="IsData"/>) or listed in <see cref="DataText"/>.
/// <para>
/// <see cref="Converted"/> is the scenarios already moved to the table; the list
/// grows with each slice until it is <c>Scenarios.All</c>.
/// </para>
/// </summary>
[NotInParallel]
public partial class PseudoLocaleTests
{
    /// <summary>
    /// The scenarios whose every string is in the table, with the part of the window
    /// that is checked: an overlay scenario shows the main window behind it, which
    /// has its own entry. Null checks the whole window.
    /// </summary>
    private static readonly Dictionary<string, Type?> Converted = new()
    {
        ["about-window"] = typeof(Views.AboutView),
        ["preferences-window"] = typeof(Views.PreferencesView),
        ["preferences-window-appearance"] = typeof(Views.PreferencesView),
        ["preferences-window-data"] = typeof(Views.PreferencesView),
        ["connection-dialog"] = null,
        ["connection-dialog-ssh-agent"] = null,
        ["connection-dialog-wide"] = null,
        ["connection-credential-warning"] = null,
        ["connection-dialog-verify-full"] = null,
        ["main-window"] = null,
        ["main-window-read-only"] = null,
        ["main-window-empty"] = null,
        ["main-window-error"] = null,
        ["main-window-script"] = null,
        // main-window-plan and -plan-tree join once Core's PlanAnalyzer warnings are in the table.
        ["main-window-palette"] = null,
        ["main-window-sidebar-filter"] = null,
        ["main-window-cell-inspector"] = null,
        ["main-window-signature-hint"] = null,
        ["main-window-completion"] = null,
        ["main-window-browse-row-details"] = null,
        ["main-window-browse-no-match"] = null,
        ["filter-editor"] = null,
        ["main-window-browse-typed-where"] = null,
        ["main-window-queries"] = null,
        ["main-window-read-only-not-applied"] = null,
        ["main-window-cell-inspector-tree"] = null,
        ["main-window-cell-inspector-edit"] = null,
        ["main-window-production"] = null,
        ["main-window-production-writes-on"] = null,
        ["shortcuts-window"] = typeof(Views.ShortcutsView),
        ["activity-window"] = null,
        ["activity-window-blocking"] = null,
        ["database-overview-window"] = null,
        ["slow-queries-window"] = null,
        ["slow-queries-window-unavailable"] = null,
        ["notify-window"] = null,
        ["save-query-dialog"] = null,
        ["staged-conflict-dialog"] = null,
        ["confirm-dialog"] = null,
        ["pending-changes-dialog"] = null,
        ["import-plan-dialog"] = null,
        ["import-dialog"] = null,
        ["crash-window"] = null,
        ["host-key-dialog"] = null,
        ["query-lab-empty"] = null,
        // query-lab-window and query-lab-plan-changes join in PR 4 (Core's plan comparison text).
        ["backup-window"] = null,
        ["backup-window-running"] = null,
        ["backup-window-done"] = null,
        ["backup-window-failed"] = null,
        ["backup-window-tools-missing"] = null,
        ["restore-window-choose"] = null,
        ["restore-window"] = null,
        ["restore-window-replace"] = null,
        ["restore-window-running"] = null,
        ["restore-window-done"] = null,
        ["restore-window-failed"] = null,
        ["restore-window-script"] = null,
        ["security-window"] = null,
        ["security-window-permissions"] = null,
        ["security-window-default-privileges"] = null,
        ["security-window-rls"] = null,
        ["security-role-dialog"] = null,
        ["security-drop-role-dialog"] = null,
        ["bulk-grant-dialog"] = null,
    };

    /// <summary>Text that is data in the fixtures, or a name never translated.</summary>
    private static readonly HashSet<string> DataText =
    [
        "pgNimbus",
        "Local shop",
        "Staging",
        "Production",
        "pgnimbus@localhost/shop",
        "pgnimbus@127.0.0.1/shop",
        "app@db.staging.example/shop",
        "reporting@db.example.com:6432/shop",
        "localhost/shop",
        "127.0.0.1/shop",
        "db.example.com/shop",
        "localhost",
        "postgres",
        "shop",
        "Ctrl+K",
        "Alt",
        "Backspace",
        "Ctrl",
        "Delete",
        "Enter",
        "Esc",
        "PgDn",
        "PgUp",
        "Shift",
        "Space",
        "Tab",
        "public",
        "analytics",
        "billing",
        "customers",
        "orders",
        "order_items",
        "products",
        "active_customers",
        "daily_revenue",
        "12.0 MB",
        "256.0 MB",
        "40.0 MB",
        "6.0 MB",
        "88.0 MB",
        "customer_id",
        "id",
        "metadata",
        "placed_at",
        "status",
        "total",
        "bigint",
        "jsonb",
        "timestamptz",
        "numeric(12,2)",
        "order_status",
        "timestamp with time zone",
        "Revenue by day",
        "Slow orders",
        "Unshipped items",
        "public.orders",
        "boolean",
        "customer",
        "numeric(10,2)",
        "paid",
        "text",
        "false",
        "shipped",
        "\"placed_at\" >= '2026-07-01 00:00:00+00'",
        "WHERE \"customer\" ILIKE '%zzz%'",
        "\"customer\" ILIKE '%zzz%'",
        "WHERE (status = 'paid' OR paid) AND (\"total\" > '50') AND (\"placed_at\" IS NOT NULL)",
        "\"placed_at\" IS NOT NULL",
        "\"total\" > '50'",
        "total > 50",
        "status = 'paid' OR paid",
        "Orders",
        "orders_customer_id_fkey",
        "Window",
        "status ≠ cancelled",
        "total ≥ 50",
        "\"status\" <> 'cancelled'",
        "\"total\" >= '50'",
        "metadata->'notify'->>1",
        // Monitoring windows
        "app@shop",
        "6 backends · 09:41:02",
        "2 backends waiting on locks · 09:41:02",
        "1.2 GB",
        "6 relations · 3 unused indexes wasting 68 MB · 09:41:02",
        "order_events",
        "cache_invalidation",
        "job_queue",
        "products:24119",
        "{\"event\":\"order.shipped\",\"order_id\":4815,\"carrier\":\"dhl\"}",
        "customers:184002",
        // Dialogs
        "Delete 3 rows? This can't be undone.",
        "2 staged changes to public.orders",
        "order_id",
        "line",
        "qty",
        "'packed'",
        "'shipped'",
        "note",
        "NULL",
        "'rush — customer called'",
        "'gift wrap'",
        "double precision",
        "bastion.example.com:2222",
        "ssh-ed25519",
        "SHA256:N1SA3QGBX6UpjLt0OmRDWW3oayFMz/ZtKDZsysTgEag",
        "orders for one customer · no index",
        "orders for one customer · with index",
        // Backup and Restore
        "prod-eu · app@db.example.com:5432/shop",
        "local · postgres@localhost:5432/shop",
        @"C:\Users\me\Documents\shop_2026-10-10_1432.dump",
        "shop_2026-10-10_1432.dump",
        "shop.sql",
        // The same file shown whole on Linux and macOS, where Path.GetFileName
        // does not split a Windows path.
        @"C:\Users\me\Documents\shop.sql",
        "query failed: ERROR:  permission denied for table payroll\ndetail: Query was: LOCK TABLE hr.payroll IN ACCESS SHARE MODE",
        "https://www.enterprisedb.com/downloads/postgres-postgresql-downloads",
        "winget install -e --id PostgreSQL.PostgreSQL.18 --interactive",
        // Security
        "PostgreSQL 17.2",
        "app_ro",
        "app_rw",
        "legacy_etl",
        "readers",
        "writers",
        "the read-only API user",
        "statement_timeout=30s",
        "sales",
        "sales.orders",
        "sales.order_items",
        "sales.customers",
        "sales.shipments",
        "email",
        "app_ro: SELECT",
        "REFERENCES",
        "TRIGGER",
        "MAINTAIN",
    ];

    /// <summary>The words of a key cap, which are written as the key is labelled.</summary>
    private static readonly HashSet<string> KeyNames =
    [
        "Ctrl", "Cmd", "Shift", "Alt", "Option", "Enter", "Return", "Esc", "Tab", "Space", "Backspace",
        "Delete", "Del", "Home", "End", "PgUp", "PgDn", "Up", "Down", "Left", "Right", "Insert",
    ];

    [GeneratedRegex(@"\p{L}{2,}")]
    private static partial Regex Word();

    // SQL a person wrote or the app shows as code: it starts with a statement keyword.
    // A script that opens with a "--" comment is SQL too (the generated drop-role script).
    [GeneratedRegex(@"^(--|(SELECT|UPDATE|INSERT|DELETE|WITH|WHERE|VACUUM|ANALYZE|EXPLAIN|CREATE|ALTER|DROP|SET|BEGIN|COMMIT|ROLLBACK|GRANT|REVOKE|TRUNCATE)\b)")]
    private static partial Regex Sql();

    // A query history row's second line ("09:41 · 18 ms · 50 rows"): written in the
    // language in use when the query ran, and kept as written, like a status line.
    [GeneratedRegex(@"^\d{1,2}:\d{2} · ")]
    private static partial Regex HistoryLine();

    // A script section's chip: its number and the statement's keyword ("2 · SELECT").
    [GeneratedRegex(@"^\d+ · [A-Z ]+$")]
    private static partial Regex ScriptChip();

    /// <summary>
    /// Built in the pseudo-locale, every string must come from the table. Built in
    /// English and switched while open (<paramref name="switchWhileOpen"/>), every
    /// string must also follow the switch: text computed once and kept is what this
    /// finds, the way a person changing the language in Settings would.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Every_visible_string_in_a_converted_window_comes_from_the_table(bool switchWhileOpen)
    {
        try
        {
            await Ui.Run(async () =>
            {
                var leftovers = new List<string>();
                var scanned = 0;
                foreach (var (name, build) in Scenarios.All.Where(s => Converted.ContainsKey(s.Name)))
                {
                    Localizer.Load(switchWhileOpen ? Localizer.English : Localizer.Pseudo);
                    var window = build();
                    Ui.Show(window);
                    Ui.Settle();
                    if (switchWhileOpen)
                    {
                        Localizer.Load(Localizer.Pseudo);
                        Ui.Settle();
                    }

                    Visual root = Converted[name] is { } scope
                        ? window.GetVisualDescendants().First(v => v.GetType() == scope)
                        : window;

                    // Every tab of every tab strip, not only the one the scenario opened on.
                    var pages = root.GetVisualDescendants().OfType<TabControl>().ToList();
                    var states = new List<Action> { () => { } };
                    foreach (var tabs in pages)
                    {
                        for (var i = 0; i < tabs.ItemCount; i++)
                        {
                            var index = i;
                            states.Add(() => tabs.SelectedIndex = index);
                        }
                    }

                    foreach (var state in states)
                    {
                        state();
                        Ui.Settle();
                        foreach (var (control, where, text) in Texts(root))
                        {
                            // A status line is written when something happens and kept as
                            // written, like a log line (CLAUDE.md, "Localization").
                            if (switchWhileOpen && IsStatusLine(control))
                            {
                                continue;
                            }

                            scanned++;
                            var line = $"{name}: {where}: \"{text}\"";
                            if (!IsTranslated(text) && !leftovers.Contains(line))
                            {
                                leftovers.Add(line);
                            }
                        }
                    }

                    window.Close();
                    Ui.Settle();

                    // The Localizer holds its observers weakly, and a closed window's
                    // stay in its tables until a collection: without one, every later
                    // switch also updated every earlier scenario's controls, and the
                    // live-switch pass took 13 minutes on a CI runner.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                await Assert.That(scanned).IsGreaterThan(Converted.Count);
                await Assert.That(leftovers).IsEmpty().Because(string.Join(Environment.NewLine, leftovers));
            });
        }
        finally
        {
            await Ui.Run(() =>
            {
                Localizer.Load(Localizer.English);
                return Task.CompletedTask;
            });
        }
    }

    private static bool IsTranslated(string text) =>
        !Word().IsMatch(text) || text.Contains('[') || DataText.Contains(text.Trim())
        || Sql().IsMatch(text) || HistoryLine().IsMatch(text) || ScriptChip().IsMatch(text) || IsKeys(text);

    /// <summary>A gesture as the key caps spell it: "Ctrl+Shift+F / Alt+Shift+F", "F5", "Ctrl+K".</summary>
    private static bool IsKeys(string text) =>
        text.Split(['+', '/', ' '], StringSplitOptions.RemoveEmptyEntries)
            .All(part => KeyNames.Contains(part) || part.Length <= 2 || (part[0] == 'F' && part[1..].All(char.IsAsciiDigit)));

    private static bool IsStatusLine(Visual? control) =>
        control is not null && control.GetVisualAncestors().Prepend(control).OfType<StyledElement>()
            .Any(e => e.Classes.Contains("statusText") || e.Classes.Contains("statusMessage"));

    /// <summary>Every piece of text a window shows, with the element it is on.</summary>
    internal static IEnumerable<(Visual? Control, string Where, string Text)> Texts(Visual root)
    {
        if (root is Window { Title: { Length: > 0 } title })
        {
            yield return (null, "Window.Title", title);
        }

        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is not Control control || !control.IsEffectivelyVisible || IsData(control))
            {
                continue;
            }

            if (control is TextBlock { Text: { Length: > 0 } text })
            {
                yield return (control, Describe(control), text);
            }

            // A tooltip that is a key is drawn by StringKeyTemplate in the language in use.
            var tip = ToolTip.GetTip(control) switch
            {
                string s => s,
                StringKey key => Strings.Get(key),
                _ => null,
            };
            if (tip is not null)
            {
                yield return (control, Describe(control) + " tooltip", tip);
            }

            if (control is TextBox { PlaceholderText: { Length: > 0 } placeholder })
            {
                yield return (control, Describe(control) + " placeholder", placeholder);
            }
        }
    }

    /// <summary>
    /// Text drawn where only data goes: a grid cell or column header, an editor, the
    /// text a person typed into a box, a query plan (the server's EXPLAIN output, as
    /// text or as the tree's nodes), a JSON value's tree, and completion items (catalog
    /// names and SQL words).
    /// </summary>
    private static bool IsData(Control control) =>
        control.GetVisualAncestors().Any(a => a is DataGridCell or DataGridColumnHeader or AvaloniaEdit.TextEditor or TextPresenter or Views.ReadOnlyTextView)
        || control is TextPresenter
        || control is Completion.CompletionLabel
        || control.DataContext is ViewModels.ExplainNodeViewModel or Core.Json.JsonTreeNode;

    private static string Describe(Control control)
    {
        var named = control.GetVisualAncestors().OfType<Control>().Prepend(control).FirstOrDefault(c => !string.IsNullOrEmpty(c.Name));
        return control.GetType().Name + (named is null ? "" : " in " + named.Name);
    }
}
