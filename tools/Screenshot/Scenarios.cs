using Avalonia.Controls;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Query;

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
internal static class Scenarios
{
    private const string SampleSql = """
        SELECT o.id,
               c.full_name AS customer,
               o.status,
               o.total,
               o.metadata,
               o.placed_at
          FROM orders AS o
          JOIN customers AS c
            ON c.id = o.customer_id
         WHERE o.placed_at > now() - interval '30 days'
         ORDER BY o.placed_at DESC;
        """;

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

    /// <summary>The F1 keyboard cheat sheet (projected from the command catalog).</summary>
    public static Window Shortcuts() => new ShortcutsWindow { Width = 900, Height = 760 };

    /// <summary>The preferences page.</summary>
    public static Window Preferences() =>
        new PreferencesWindow { DataContext = new PreferencesViewModel(Fixtures.MainWindowViewModel()), Width = 720, Height = 640 };

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
