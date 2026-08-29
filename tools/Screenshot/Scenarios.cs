using Avalonia.Controls;
using PgNimbus.App.ViewModels;
using PgNimbus.App.ViewModels.Security;
using PgNimbus.App.Views;
using PgNimbus.App.Views.Security;
using Avalonia.VisualTree;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Query;
using PgNimbus.Core.Security;

namespace PgNimbus.Screenshot;

/// <summary>
/// One scenario per interesting UI state. Each returns a ready-to-show
/// <see cref="Window"/>; <c>Program</c> renders it in both themes.
///
/// Scenarios drive the ViewModels through the same public surface production
/// uses (set the property, run the command) rather than poking at views, so a
/// screenshot shows what the app would actually render — the one concession is
/// that data arrives from <see cref="Fixtures"/> instead of a server.
/// </summary>
public static class Scenarios
{
    private const string SampleSql = """
        SELECT o.id,
               c.full_name AS customer,
               o.status,
               o.total,
               o.paid,
               o.metadata,
               o.placed_at
          FROM orders AS o
          JOIN customers AS c
            ON c.id = o.customer_id
         WHERE o.placed_at > now() - interval '30 days'
         ORDER BY o.placed_at DESC;
        """;

    /// <summary>
    /// Every scenario the harness renders, in the order it renders them. The
    /// single list: <c>Program</c> walks it, the baseline set is exactly its
    /// names x {light,dark}, and the marketing shots in <see cref="Marketing"/>
    /// pick their sources out of it by name.
    /// </summary>
    public static readonly (string Name, Func<Window> Build)[] All =
    [
        ("main-window", Results),
        ("main-window-empty", EmptyResults),
        ("main-window-error", QueryError),
        ("main-window-script", ScriptResult),
        ("main-window-plan", QueryPlan),
        ("main-window-plan-tree", QueryPlanTree),
        ("main-window-palette", CommandPalette),
        ("main-window-sidebar-filter", SidebarFilter),
        ("main-window-cell-inspector", CellInspector),
        ("activity-window", Activity),
        ("activity-window-blocking", ActivityBlocking),
        ("database-overview-window", DatabaseOverview),
        ("notify-window", NotifyMonitor),
        ("security-window", Security),
        ("security-window-permissions", SecurityPermissions),
        ("security-window-default-privileges", SecurityDefaultPrivileges),
        ("security-window-rls", SecurityRls),
        ("security-role-dialog", SecurityRoleDialog),
        ("security-drop-role-dialog", SecurityDropRoleDialog),
        ("save-query-dialog", SaveQueryDialogShot),
        ("shortcuts-window", Shortcuts),
        ("preferences-window", Preferences),
        ("about-window", About),
        ("crash-window", Crash),
        ("connection-dialog", ConnectionDialog),
    ];

    // --- Main window ------------------------------------------------------

    /// <summary>The default view: schema tree, a query, and its result grid.</summary>
    public static Window Results()
    {
        var vm = Fixtures.MainWindowViewModel();
        SeedOrdersResult(vm.ActiveTab);
        return HostMainWindow(vm);
    }

    /// <summary>A tab that has never run — the results pane's empty state.</summary>
    public static Window EmptyResults()
    {
        var vm = Fixtures.MainWindowViewModel();
        vm.ActiveTab.Sql = SampleSql;
        return HostMainWindow(vm);
    }

    /// <summary>A failed run: the error status line and the identifier-fix offer.</summary>
    public static Window QueryError()
    {
        var vm = Fixtures.MainWindowViewModel();
        var tab = vm.ActiveTab;
        tab.Sql = "SELECT * FROM Orders WHERE placed_at > now() - interval '30 days';";
        tab.Status = "Error: relation \"orders\" does not exist";
        tab.HasError = true;
        return HostMainWindow(vm);
    }

    /// <summary>A multi-statement script: the per-statement section strip above the grid.</summary>
    public static Window ScriptResult()
    {
        var vm = Fixtures.MainWindowViewModel();
        var tab = vm.ActiveTab;
        tab.Sql = "SET work_mem = '64MB';\n\n" + SampleSql + "\n\nUPDATE orders SET status = 'shipped' WHERE id = 4801;";

        var (columns, rows) = Fixtures.OrdersResult();
        tab.ResultSections.Add(ScriptResultViewModel.From(1, "SET work_mem = '64MB'", new CommandResult
        {
            Elapsed = TimeSpan.FromMilliseconds(1.2),
            RowsAffected = 0,
            CommandTag = "SET",
        }));
        tab.ResultSections.Add(ScriptResultViewModel.From(2, SampleSql, new MaterializedResultSet
        {
            Elapsed = TimeSpan.FromMilliseconds(18.4),
            Columns = columns,
            Rows = rows,
        }));
        tab.ResultSections.Add(ScriptResultViewModel.From(3, "UPDATE orders SET status = 'shipped' WHERE id = 4801", new CommandResult
        {
            Elapsed = TimeSpan.FromMilliseconds(4.1),
            RowsAffected = 1,
            CommandTag = "UPDATE",
        }));
        tab.SelectedSection = tab.ResultSections[1];
        return HostMainWindow(vm);
    }

    /// <summary>The plan's text layout plus the warnings strip.</summary>
    public static Window QueryPlan() => PlanWindow(asTree: false);

    /// <summary>The graphical plan tree: time-heat bars, bottleneck node, metric toggle.</summary>
    public static Window QueryPlanTree() => PlanWindow(asTree: true);

    private static Window PlanWindow(bool asTree)
    {
        var vm = Fixtures.MainWindowViewModel();
        var tab = vm.ActiveTab;
        tab.Sql = SampleSql;

        var plan = ExplainService.Import(File.ReadAllText(FixturePath("plan-analyze.json")));
        tab.ShowImportedPlan(plan.Result, plan.DisplayText, plan.RawJson);
        tab.IsPlanTextView = !asTree;
        return HostMainWindow(vm);
    }

    /// <summary>The command palette over a populated window.</summary>
    public static Window CommandPalette()
    {
        var vm = Fixtures.MainWindowViewModel();
        SeedOrdersResult(vm.ActiveTab);

        // Fire-and-forget on purpose: the palette opens synchronously with its
        // action/saved-query entries, and the catalog fetch it then starts never
        // completes against the offline data source (see Fixtures).
        _ = vm.OpenCommandPaletteAsync();
        return HostMainWindow(vm);
    }

    /// <summary>Roles: the list, one role's attributes, and its membership tree.</summary>
    public static Window Security()
    {
        var vm = Fixtures.SecurityViewModel();
        return new SecurityWindow { DataContext = vm, Width = 1100, Height = 720 };
    }

    /// <summary>
    /// The permissions matrix, on the tab that is the feature's whole point:
    /// SELECT that app_ro holds only through a group two memberships up, with
    /// the source spelled out rather than left to be inferred from an ACL.
    /// </summary>
    public static Window SecurityPermissions()
    {
        var vm = Fixtures.SecurityViewModel();
        var window = new SecurityWindow { DataContext = vm, Width = 1100, Height = 720 };
        window.Opened += (_, _) => SelectTab(window, 1);
        return window;
    }

    /// <summary>
    /// Default privileges and row-level security, unseeded on purpose: both tabs
    /// have to say something useful when the catalog has nothing to show, and an
    /// empty state that renders as a blank rectangle is the failure these two
    /// shots exist to catch.
    /// </summary>
    public static Window SecurityDefaultPrivileges()
    {
        var window = new SecurityWindow { DataContext = Fixtures.SecurityViewModel(), Width = 1100, Height = 720 };
        window.Opened += (_, _) => SelectTab(window, 2);
        return window;
    }

    public static Window SecurityRls()
    {
        var window = new SecurityWindow { DataContext = Fixtures.SecurityViewModel(), Width = 1100, Height = 720 };
        window.Opened += (_, _) => SelectTab(window, 3);
        return window;
    }

    /// <summary>
    /// The role editor, mid-edit. Worth its own shot because it is the one
    /// surface in this feature that writes, and because its generated-SQL
    /// preview is always the masked build — a password appearing here would be
    /// the bug.
    /// </summary>
    public static Window SecurityRoleDialog()
    {
        var host = Fixtures.SecurityViewModel();
        var editor = RoleEditorViewModel.ForCreate(new SecurityEditor(Fixtures.DataSource), host);
        editor.Name = "reporting_ro";
        editor.Password = "hunter2";
        editor.PasswordConfirm = "hunter2";
        editor.ConnectionLimit = 10;
        editor.Comment = "read-only user for the reporting job";
        return new RoleDialog { DataContext = editor, Width = 760, Height = 660 };
    }

    /// <summary>
    /// The drop-role dialog on a role that blocks nothing — the empty state,
    /// deliberately, because both of its lists are usually empty and a grid
    /// drawing bare column headers over the sentence explaining that is exactly
    /// the failure this shot catches.
    /// </summary>
    public static Window SecurityDropRoleDialog()
    {
        var host = Fixtures.SecurityViewModel();
        var drop = new DropRoleViewModel(
            new RoleService(Fixtures.DataSource),
            new SecurityEditor(Fixtures.DataSource),
            "legacy_etl",
            "postgres",
            ["postgres", "app_rw", "readers"],
            null)
        {
            // LoadAsync is the view's job on Opened; the harness stands in for it
            // with the answer a role that owns nothing would have produced.
            IsLoading = false,
        };

        return new DropRoleDialog { DataContext = drop, Width = 820, Height = 680 };
    }

    /// <summary>
    /// Naming a query on its way into the Saved Queries list, caught in its
    /// name-already-taken state — the one branch with anything to look at, and
    /// the one that keeps the list from filling with rows sharing a name.
    /// </summary>
    public static Window SaveQueryDialogShot()
    {
        var taken = new SavedQuery(Guid.NewGuid(), "Daily revenue", "SELECT 1;", DateTimeOffset.Now);
        return new SaveQueryDialog(
            "Save query",
            taken.Name,
            currentId: null,
            name => string.Equals(name.Trim(), taken.Name, StringComparison.OrdinalIgnoreCase) ? taken : null)
        {
            Width = 420,
            Height = 260,
        };
    }

    /// <summary>Selects a tab by index once the window's template is up.</summary>
    private static void SelectTab(Window window, int index)
    {
        if (window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is { } tabs)
        {
            tabs.SelectedIndex = index;
        }
    }

    /// <summary>The sidebar filter narrowing the tree to matching relations.</summary>
    public static Window SidebarFilter()
    {
        var vm = Fixtures.MainWindowViewModel();
        SeedOrdersResult(vm.ActiveTab);
        vm.SchemaTree.FilterText = "order";
        return HostMainWindow(vm);
    }

    /// <summary>The cell inspector over a jsonb value, in read mode.</summary>
    public static Window CellInspector()
    {
        var vm = Fixtures.MainWindowViewModel();
        SeedOrdersResult(vm.ActiveTab);
        vm.CellInspector.Open("metadata", """{"channel":"web","coupon":"SUMMER26","items":[{"sku":"NIM-1","qty":2}]}""");
        return HostMainWindow(vm);
    }

    // --- Secondary windows ------------------------------------------------

    /// <summary>Server Activity, backends tab.</summary>
    public static Window Activity() => BuildActivityWindow(tab: 0);

    /// <summary>Server Activity, who-blocks-whom tree.</summary>
    public static Window ActivityBlocking() => BuildActivityWindow(tab: 1);

    private static Window BuildActivityWindow(int tab)
    {
        var vm = Fixtures.MainWindowViewModel().Activity;
        foreach (var backend in Fixtures.Backends())
        {
            vm.Rows.Add(new ActivityRow(backend));
        }

        foreach (var root in BlockingTree.Build(Fixtures.BlockingBackends()))
        {
            vm.BlockingRoots.Add(new BlockingNode(root));
        }

        vm.HasLockWaits = true;
        vm.Status = $"{vm.Rows.Count} backends · 09:41:02";
        vm.BlockingStatus = "2 backends waiting on locks · 09:41:02";
        vm.AutoRefresh = false;
        vm.SelectedTab = tab;
        vm.SelectedRow = vm.Rows[1];
        vm.SelectedBlockingNode = vm.BlockingRoots[0];

        return new ActivityWindow { DataContext = vm, Width = 1100, Height = 700 };
    }

    /// <summary>Database Overview: sizes, cache-hit ratios, scan usage, unused indexes.</summary>
    public static Window DatabaseOverview()
    {
        var vm = Fixtures.MainWindowViewModel().DatabaseOverview;
        vm.DatabaseName = "shop";
        vm.DatabaseSizeText = "1.2 GB";
        vm.TableCacheHitText = "99.1 %";
        vm.IndexCacheHitText = "99.8 %";

        foreach (var relation in Fixtures.LargestRelations())
        {
            vm.LargestRelations.Add(new RelationSizeRow(relation));
        }

        foreach (var scan in Fixtures.TableScans())
        {
            vm.TableScans.Add(new TableScanRow(scan));
        }

        foreach (var index in Fixtures.UnusedIndexes())
        {
            vm.UnusedIndexes.Add(new UnusedIndexRow(index));
        }

        vm.Status = "6 relations · 3 unused indexes wasting 68 MB · 09:41:02";
        return new DatabaseOverviewWindow { DataContext = vm, Width = 1100, Height = 760 };
    }

    /// <summary>
    /// The LISTEN/NOTIFY monitor: channels subscribed, a live feed, and the
    /// selected payload pretty-printed in the detail pane — the shot exists
    /// because that pane is the whole argument for the window (a JSON payload
    /// was previously a trimmed one-liner in a sidebar).
    /// </summary>
    public static Window NotifyMonitor()
    {
        var vm = Fixtures.MainWindowViewModel().NotifyMonitor;

        foreach (var channel in new[] { "order_events", "cache_invalidation", "job_queue" })
        {
            vm.ChannelName = channel;
            vm.AddChannelCommand.Execute(null);
        }

        // Oldest first, so the feed ends up newest-first the way the live one does.
        foreach (var notification in Fixtures.Notifications().Reverse())
        {
            vm.SeedNotification(notification);
        }

        vm.IsListening = true;
        vm.SelectedChannel = "order_events";
        vm.SelectedNotification = vm.Notifications[0];

        return new NotifyMonitorWindow { DataContext = vm, Width = 1040, Height = 620 };
    }

    /// <summary>
    /// The F1 keyboard cheat sheet (projected from the command catalog), the
    /// preferences page and the About box — all three OverlayPanels over the shell
    /// rather than windows of their own, so all three are rendered by opening the
    /// shell with one of them up.
    /// </summary>
    public static Window Shortcuts() => OverlayOn(vm => vm.IsShortcutsOpen = true);

    /// <summary>The preferences page.</summary>
    public static Window Preferences() => OverlayOn(vm => vm.IsPreferencesOpen = true);

    /// <summary>The About box.</summary>
    public static Window About() => OverlayOn(vm => vm.IsAboutOpen = true);

    private static Window OverlayOn(Action<MainViewModel> open)
    {
        var vm = Fixtures.MainWindowViewModel();
        open(vm);
        return HostMainWindow(vm);
    }

    /// <summary>
    /// The connection picker, with a few saved profiles.
    ///
    /// Both stores are pointed at a throwaway directory that does not exist:
    /// their real paths are the developer's own <c>connections.json</c> and
    /// saved passwords, and a screenshot run must never read — let alone
    /// publish — either.
    /// </summary>
    public static Window ConnectionDialog()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "pgnimbus-fixtures", Guid.NewGuid().ToString("N"));
        var viewModel = new ConnectionDialogViewModel(
            new ConnectionProfileStore(Path.Combine(scratch, "connections.json")),
            new PlainFileCredentialStore(Path.Combine(scratch, "credentials")));

        foreach (var profile in Fixtures.ConnectionProfiles())
        {
            viewModel.Profiles.Add(profile);
        }

        viewModel.SelectedProfile = viewModel.Profiles[0];
        return new PgNimbus.App.Views.ConnectionDialog { DataContext = viewModel, Width = 640, Height = 680 };
    }

    /// <summary>The crash reporter, with a representative failure.</summary>
    public static Window Crash() =>
        new CrashWindow(
            new InvalidOperationException("The connection pool has been shut down."),
            Path.Combine("C:", "Users", "you", "AppData", "Roaming", "pgNimbus", "logs", "pgnimbus.log"))
        {
            Width = 720,
            Height = 480,
        };

    // --- Helpers ----------------------------------------------------------

    /// <summary>
    /// The shell with the fixture catalog and a result set already in the grid,
    /// handed back together with its view model.
    ///
    /// <see cref="Results"/> throws the view model away because a screenshot only
    /// needs the window; the UI tests in <c>PgNimbus.App.Tests</c> drive the same
    /// window and then have to assert on what the commands did, so they need
    /// both halves.
    /// </summary>
    public static (Window Window, MainViewModel ViewModel) Shell()
    {
        var vm = Fixtures.MainWindowViewModel();
        SeedOrdersResult(vm.ActiveTab);
        return (HostMainWindow(vm), vm);
    }

    private static void SeedOrdersResult(QueryViewModel tab)
    {
        var (columns, rows) = Fixtures.OrdersResult();
        tab.Sql = SampleSql;
        tab.SeedResult(columns, rows, rowCountText: $"{rows.Count} rows", timingText: "18 ms · first byte 6 ms");
    }

    private static Window HostMainWindow(MainViewModel viewModel) =>
        new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
