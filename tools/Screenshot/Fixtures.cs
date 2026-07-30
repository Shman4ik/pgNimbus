using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Import;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Notifications;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Screenshot;

/// <summary>
/// Builds fully-populated ViewModels with no live Postgres behind them.
///
/// Every service in the app takes an <see cref="NpgsqlDataSource"/>, and
/// <see cref="NpgsqlDataSource.Create(string)"/> opens no socket, so the whole
/// object graph constructs offline. The address it points at is deliberately
/// <em>unroutable</em> (TEST-NET-3, RFC 5737) rather than a closed local port:
/// windows that kick off a refresh when they open (activity, database overview,
/// schema tree) would otherwise get a fast connection-refused back and overwrite
/// the seeded fixture status line with an error a fraction of a second after the
/// window shows. Against an address that simply never answers, those refreshes
/// stay in flight for the life of the (very short) process and never touch the
/// seeded state.
/// </summary>
internal static class Fixtures
{
    private const string OfflineConnectionString =
        "Host=203.0.113.1;Port=5432;Database=shop;Username=pgnimbus;Timeout=300;Command Timeout=0";

    public static NpgsqlDataSource DataSource { get; } = NpgsqlDataSource.Create(OfflineConnectionString);

    /// <summary>
    /// A main-window view model wired exactly as <c>App.BuildMainWindow</c> wires
    /// one, minus the settings/workspace persistence (a screenshot run must not
    /// read or write the developer's real app data) and minus the catalog refresh
    /// it kicks off — the schema tree is seeded here instead.
    /// </summary>
    public static MainViewModel MainWindowViewModel()
    {
        var dataSource = DataSource;
        var schemaService = new SchemaService(dataSource);
        var schemaTree = new SchemaTreeViewModel(schemaService, showSizes: true);

        var viewModel = new MainViewModel(
            new QueryEngine(dataSource),
            new ExplainService(dataSource),
            schemaTree,
            schemaService,
            new SchemaEditor(dataSource),
            new DdlService(dataSource),
            new SqlCompletionProvider(schemaService),
            new NotifyMonitorViewModel(new NotificationListener(dataSource)),
            new ActivityService(dataSource),
            new DatabaseStatsService(dataSource),
            new ImportService(dataSource),
            connectionHost: "localhost",
            connectionDatabase: "shop");

        // SavedQueriesViewModel loads the real on-disk saved queries and history
        // in its constructor (MainViewModel news up the stores itself, so there
        // is nothing to inject). Drop whatever that pulled in before it can reach
        // a screenshot, and seed the fixture lists instead.
        viewModel.SavedQueries.SavedQueries.Clear();
        viewModel.SavedQueries.History.Clear();
        SeedSavedQueries(viewModel.SavedQueries);

        SeedSchemaTree(schemaTree, schemaService);
        return viewModel;
    }

    // --- Schema tree ------------------------------------------------------

    /// <summary>
    /// A small but realistic catalog: two schemas, a mix of relation kinds, and
    /// columns on the tables the scenarios expand. Nodes are seeded rather than
    /// loaded, so expanding one never reaches for the catalog.
    /// </summary>
    private static void SeedSchemaTree(SchemaTreeViewModel tree, SchemaService schemaService)
    {
        var publicSchema = Schema(schemaService, tree, "public",
            Table(schemaService, "public", "orders", RelationKind.Table, 268_435_456, Columns(
                ("id", "bigint", true, true),
                ("customer_id", "bigint", true, false),
                ("status", "order_status", true, false),
                ("total", "numeric(12,2)", true, false),
                ("metadata", "jsonb", false, false),
                ("placed_at", "timestamp with time zone", true, false))),
            Table(schemaService, "public", "order_items", RelationKind.Table, 92_274_688, Columns(
                ("id", "bigint", true, true),
                ("order_id", "bigint", true, false),
                ("product_id", "bigint", true, false),
                ("quantity", "integer", true, false),
                ("unit_price", "numeric(12,2)", true, false))),
            Table(schemaService, "public", "customers", RelationKind.Table, 41_943_040, Columns(
                ("id", "bigint", true, true),
                ("email", "citext", true, false),
                ("full_name", "text", true, false),
                ("created_at", "timestamp with time zone", true, false))),
            Table(schemaService, "public", "products", RelationKind.Table, 12_582_912, Columns(
                ("id", "bigint", true, true),
                ("sku", "text", true, false),
                ("name", "text", true, false),
                ("price", "numeric(12,2)", true, false),
                ("tags", "text[]", false, false))),
            Table(schemaService, "public", "active_customers", RelationKind.View, null, Columns(
                ("id", "bigint", false, false),
                ("email", "citext", false, false))),
            Table(schemaService, "public", "daily_revenue", RelationKind.MaterializedView, 6_291_456, Columns(
                ("day", "date", false, false),
                ("revenue", "numeric", false, false))));

        var analyticsSchema = Schema(schemaService, tree, "analytics",
            Table(schemaService, "analytics", "events", RelationKind.PartitionedTable, null, Columns(
                ("id", "bigint", true, true),
                ("occurred_at", "timestamp with time zone", true, false),
                ("payload", "jsonb", false, false))),
            Table(schemaService, "analytics", "sessions", RelationKind.Table, 734_003_200, Columns(
                ("id", "uuid", true, true),
                ("customer_id", "bigint", false, false),
                ("started_at", "timestamp with time zone", true, false))));

        tree.Schemas.Add(publicSchema);
        tree.Schemas.Add(analyticsSchema);
        tree.Schemas.Add(new RolesGroupNode(schemaService));

        publicSchema.IsExpanded = true;
    }

    private static SchemaNode Schema(SchemaService schemaService, SchemaTreeViewModel tree, string name, params TableNode[] tables)
    {
        var node = new SchemaNode(schemaService, name, () => tree.ShowAdvancedObjects, () => tree.ShowSizes);
        node.SeedChildren(tables);
        return node;
    }

    private static TableNode Table(
        SchemaService schemaService, string schema, string name, RelationKind kind, long? totalBytes,
        IEnumerable<ColumnDetail> columns)
    {
        var node = new TableNode(schemaService, schema, name, kind, totalBytes, static () => true, static () => false);
        node.SeedChildren(columns.Select(c => new ColumnNode(c)));
        return node;
    }

    private static IEnumerable<ColumnDetail> Columns(params (string Name, string Type, bool NotNull, bool PrimaryKey)[] columns) =>
        columns.Select(c => new ColumnDetail(c.Name, c.Type, c.NotNull, c.PrimaryKey));

    // --- Saved queries + history ------------------------------------------

    private static void SeedSavedQueries(SavedQueriesViewModel saved)
    {
        saved.SavedQueries.Add(new SavedQuery(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Revenue by day", "SELECT day, revenue\n  FROM daily_revenue\n ORDER BY day DESC;"));
        saved.SavedQueries.Add(new SavedQuery(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Slow orders", "SELECT *\n  FROM orders\n WHERE placed_at > now() - interval '1 day';"));
        saved.SavedQueries.Add(new SavedQuery(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Unshipped items", "SELECT o.id, count(*)\n  FROM orders o\n  JOIN order_items i ON i.order_id = o.id\n WHERE o.status = 'pending'\n GROUP BY o.id;"));

        var now = new DateTimeOffset(2026, 7, 30, 9, 41, 0, TimeSpan.Zero);
        saved.History.Add(new QueryHistoryEntry("SELECT * FROM orders ORDER BY placed_at DESC LIMIT 50;", now, 18.4, "50 rows") { Connection = "localhost/shop" });
        saved.History.Add(new QueryHistoryEntry("UPDATE orders SET status = 'shipped' WHERE id = 4821;", now.AddMinutes(-6), 4.1, "1 row affected") { Connection = "localhost/shop" });
        saved.History.Add(new QueryHistoryEntry("SELECT count(*) FROM analytics.events;", now.AddMinutes(-22), 942.0, "1 row") { Connection = "localhost/shop" });
    }

    // --- Result sets ------------------------------------------------------

    /// <summary>The orders result set the grid scenarios show.</summary>
    public static (IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<object?[]> Rows) OrdersResult()
    {
        IReadOnlyList<ColumnInfo> columns =
        [
            new("id", "bigint", typeof(long), TableOid, 1),
            new("customer", "text", typeof(string), TableOid, 2),
            new("status", "order_status", typeof(string), TableOid, 3),
            new("total", "numeric", typeof(decimal), TableOid, 4),
            new("metadata", "jsonb", typeof(string), TableOid, 5),
            new("placed_at", "timestamp with time zone", typeof(DateTime), TableOid, 6),
        ];

        var statuses = new[] { "shipped", "pending", "cancelled", "shipped", "paid" };
        var names = new[]
        {
            "Ada Lovelace", "Grace Hopper", "Alan Turing", "Karen Spärck Jones", "Barbara Liskov",
            "Edsger Dijkstra", "Frances Allen", "Ken Thompson", "Radia Perlman", "Leslie Lamport",
            "Margaret Hamilton", "Donald Knuth", "Jean Bartik", "Tony Hoare", "Adele Goldberg",
            "Vint Cerf", "Shafi Goldwasser", "Bjarne Stroustrup", "Anita Borg", "Dennis Ritchie",
        };

        var placed = new DateTime(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);
        var rows = new List<object?[]>();
        for (var i = 0; i < names.Length; i++)
        {
            rows.Add(
            [
                4801L + i,
                names[i],
                statuses[i % statuses.Length],
                decimal.Round(18.5m + i * 37.25m, 2),
                $$"""{"channel": "web", "coupon": {{(i % 3 == 0 ? "\"SUMMER26\"" : "null")}}}""",
                placed.AddMinutes(-7 * i),
            ]);
        }

        return (columns, rows);
    }

    // Any non-zero oid: it only has to be consistent across the columns for the
    // result to look like it reads straight through from one table.
    private const uint TableOid = 16_421;

    // --- Monitoring -------------------------------------------------------

    public static IReadOnlyList<BackendActivity> Backends() =>
    [
        new(4821, "app", "shop", "pgNimbus", "10.0.0.14", "active", null, null, 0.4, "SELECT * FROM orders WHERE placed_at > now() - interval '1 day'"),
        new(4822, "app", "shop", "shop-api", "10.0.0.21", "active", "Lock", "transactionid", 31.7, "UPDATE orders SET status = 'shipped' WHERE id = 4821"),
        new(4823, "app", "shop", "shop-api", "10.0.0.22", "active", "Lock", "transactionid", 12.9, "UPDATE orders SET status = 'paid' WHERE id = 4821"),
        new(4830, "reporting", "shop", "metabase", "10.0.0.44", "active", "IO", "DataFileRead", 184.2, "SELECT day, sum(revenue) FROM daily_revenue GROUP BY day"),
        new(4844, "app", "shop", "shop-worker", "10.0.0.31", "idle in transaction", null, null, 96.0, "BEGIN"),
        new(4851, "postgres", "shop", "psql", null, "idle", null, null, 0.0, "SELECT 1"),
    ];

    public static IReadOnlyList<BlockingBackend> BlockingBackends() =>
    [
        new(4844, "app", "shop", "shop-worker", "idle in transaction", null, null, 96.0, "UPDATE orders SET status = 'paid' WHERE id = 4821", [], null, null),
        new(4822, "app", "shop", "shop-api", "active", "Lock", "transactionid", 31.7, "UPDATE orders SET status = 'shipped' WHERE id = 4821", [4844], "orders", "RowExclusiveLock"),
        new(4823, "app", "shop", "shop-api", "active", "Lock", "transactionid", 12.9, "UPDATE order_items SET quantity = 2 WHERE order_id = 4821", [4822], "order_items", "RowExclusiveLock"),
    ];

    public static IReadOnlyList<RelationSize> LargestRelations() =>
    [
        new("analytics", "sessions", RelationKind.Table, 734_003_200, 612_368_384, 121_634_816, 4_182_004),
        new("public", "orders", RelationKind.Table, 268_435_456, 201_326_592, 67_108_864, 1_204_881),
        new("public", "order_items", RelationKind.Table, 92_274_688, 71_303_168, 20_971_520, 3_901_223),
        new("public", "customers", RelationKind.Table, 41_943_040, 33_554_432, 8_388_608, 184_002),
        new("public", "products", RelationKind.Table, 12_582_912, 10_485_760, 2_097_152, 24_119),
        new("public", "daily_revenue", RelationKind.MaterializedView, 6_291_456, 6_291_456, 0, 1_460),
    ];

    public static IReadOnlyList<TableScanUsage> TableScans() =>
    [
        new("public", "orders", 412, 498_120_004, 1_284_902, 4_012_884, 1_204_881, 18_402),
        new("analytics", "sessions", 88_401, 3_910_442_881, 12_004, 41_002, 4_182_004, 902_144),
        new("public", "order_items", 92, 8_120_004, 4_012_884, 12_884_112, 3_901_223, 4_012),
        new("public", "customers", 1_204, 218_120_004, 902_884, 1_884_112, 184_002, 1_204),
        new("public", "products", 8_402, 202_884, 12_884, 88_112, 24_119, 88),
    ];

    public static IReadOnlyList<UnusedIndex> UnusedIndexes() =>
    [
        new("public", "orders", "orders_metadata_gin_idx", 41_943_040),
        new("analytics", "sessions", "sessions_started_at_idx", 20_971_520),
        new("public", "customers", "customers_full_name_idx", 8_388_608),
    ];
}
