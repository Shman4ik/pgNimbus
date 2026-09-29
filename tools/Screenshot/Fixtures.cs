using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.App.ViewModels;
using PgNimbus.App.ViewModels.Security;
using PgNimbus.Core.Import;
using PgNimbus.Core.Monitoring;
using PgNimbus.Core.Notifications;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Security;
using PgNimbus.Core.Settings;

namespace PgNimbus.Screenshot;

// SslMode is declared by both Npgsql and PgNimbus.Core.Connections; profiles
// carry the app's own enum.
using SslMode = PgNimbus.Core.Connections.SslMode;

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
public static class Fixtures
{
    private const string OfflineConnectionString =
        "Host=203.0.113.1;Port=5432;Database=shop;Username=pgnimbus;Timeout=300;Command Timeout=0";

    public static NpgsqlDataSource DataSource { get; } = NpgsqlDataSource.Create(OfflineConnectionString);

    /// <summary>
    /// A main-window view model wired exactly as <c>App.BuildMainWindow</c> wires
    /// one, minus the settings/workspace persistence (a screenshot run must not
    /// read or write the developer's real app data) and minus the catalog refresh
    /// it kicks off — the schema tree is seeded here instead.
    ///
    /// Its saved-query and history stores point at a fresh directory of their
    /// own under <see cref="IsolatedAppData"/>, so each view model starts with
    /// nothing on disk, and a test that saves a query writes there. They used
    /// to be the default stores: the fixture cleared what they had loaded, but
    /// the next save wrote the fixture list over the user's real file.
    ///
    /// <paramref name="workspace"/> is a snapshot to restore, handed over the
    /// way <c>App</c> hands over the one it read from <c>workspace.json</c>.
    /// </summary>
    public static MainViewModel MainWindowViewModel(WorkspaceEntry? workspace = null, bool readOnlyProfile = false)
    {
        var dataDirectory = IsolatedAppData.NewDirectory("main-window");
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
            new StatementStatsService(dataSource),
            new RoleService(dataSource),
            new PrivilegeService(dataSource),
            new SecurityEditor(dataSource),
            new ImportService(dataSource),
            connectionHost: "localhost",
            connectionDatabase: "shop",
            workspace: workspace,
            readOnlyConnection: readOnlyProfile,
            savedQueryStore: new SavedQueryStore(Path.Combine(dataDirectory, "saved-queries.json")),
            historyStore: new QueryHistoryStore(Path.Combine(dataDirectory, "history.json")));

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

        // A schema another team owns, kept out of editor completion from its
        // context menu: it stays in the tree, dimmed and eye-off marked. Seeded
        // here so that state is in the rendered screenshots too.
        var billingSchema = Schema(schemaService, tree, "billing",
            Table(schemaService, "billing", "invoices", RelationKind.Table, 20_971_520, Columns(
                ("id", "bigint", true, true),
                ("customer_id", "bigint", true, false))));
        billingSchema.ExcludedFromCompletion = true;

        tree.Schemas.Add(publicSchema);
        tree.Schemas.Add(analyticsSchema);
        tree.Schemas.Add(billingSchema);
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
        // History rows say "09:41", "Yesterday 14:02": relative to this, not to
        // the day a baseline happens to be rendered.
        App.Converters.HistoryText.Now = () => now.AddMinutes(4);
        saved.History.Add(new QueryHistoryEntry("SELECT * FROM orders ORDER BY placed_at DESC LIMIT 50;", now, 18.4, "50 rows") { Connection = "localhost/shop" });
        saved.History.Add(new QueryHistoryEntry("UPDATE orders SET status = 'shipped' WHERE id = 4821;", now.AddMinutes(-6), 4.1, "1 row affected") { Connection = "localhost/shop" });
        saved.History.Add(new QueryHistoryEntry("SELECT count(*) FROM analytics.events;", now.AddMinutes(-22), 942.0, "1 row") { Connection = "localhost/shop" });
    }

    // --- Connection profiles ----------------------------------------------

    /// <summary>
    /// Saved connections for the picker. Fixed ids so the rendered frame is
    /// identical on every run (the visual-regression baselines depend on it),
    /// and hostnames from the documentation-only domains so no screenshot ever
    /// advertises a real server.
    /// </summary>
    public static IReadOnlyList<PgNimbus.Core.Connections.ConnectionProfile> ConnectionProfiles() =>
    [
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Local shop", "localhost", 5432, "shop", "pgnimbus", SslMode.Prefer),
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), "Staging", "db.staging.example", 5432, "shop", "app", SslMode.Require, AccentColor: "#F0A030"),
        new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), "Production", "db.example.com", 6432, "shop", "reporting", SslMode.VerifyFull, AccentColor: "#E05252", ReadOnly: true,
            RootCertificatePath: "/etc/ssl/certs/example-ca.pem"),
    ];

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
            new("paid", "boolean", typeof(bool), TableOid, 5),
            new("metadata", "jsonb", typeof(string), TableOid, 6),
            new("placed_at", "timestamp with time zone", typeof(DateTime), TableOid, 7),
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
                // true / false / NULL in turn, so the boolean column's check,
                // cross and NULL placeholder all appear in the screenshots.
                i % 3 == 2 ? null : (object)(i % 3 == 0),
                $$"""{"channel": "web", "coupon": {{(i % 3 == 0 ? "\"SUMMER26\"" : "null")}}}""",
                placed.AddMinutes(-7 * i),
            ]);
        }

        return (columns, rows);
    }

    /// <summary>The catalog view of the table <see cref="OrdersResult"/> reads from, for edit contexts and browse mode.</summary>
    public static IReadOnlyList<ColumnDetail> OrdersTableColumns() =>
    [
        new("id", "bigint", NotNull: true, IsPrimaryKey: true),
        new("customer", "text", NotNull: true, IsPrimaryKey: false),
        new("status", "order_status", NotNull: true, IsPrimaryKey: false)
        {
            Editor = ColumnValueEditor.Enum,
            EnumLabels = ["pending", "paid", "shipped", "cancelled"],
        },
        new("total", "numeric(10,2)", NotNull: true, IsPrimaryKey: false),
        new("paid", "boolean", NotNull: false, IsPrimaryKey: false) { Editor = ColumnValueEditor.Boolean },
        new("metadata", "jsonb", NotNull: false, IsPrimaryKey: false) { Editor = ColumnValueEditor.Json },
        new("placed_at", "timestamp with time zone", NotNull: true, IsPrimaryKey: false) { Editor = ColumnValueEditor.Timestamp },
    ];

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

    /// <summary>
    /// pg_stat_statements for the shop database: the usual shape of a real one,
    /// where one statement is most of the time, a slow report runs rarely, and a
    /// cheap lookup runs constantly. Times are Unspecified-kind so the window
    /// shows them as written rather than converted to the renderer's time zone.
    /// </summary>
    public static StatementStatsSnapshot SlowQueriesSnapshot()
    {
        var since = new DateTime(2026, 7, 28, 8, 0, 0, DateTimeKind.Unspecified);
        StatementStat Stat(long id, string role, string query, long calls, double totalMs, long rows, long hit, long read) =>
            new(10, role, id, true, query, calls, totalMs, rows, hit, read, since);

        return new StatementStatsSnapshot(
            new DateTime(2026, 7, 30, 9, 41, 2, DateTimeKind.Unspecified),
            since,
            Deallocations: 0,
            [
                Stat(1, "app", "SELECT o.id, o.status, o.total, o.placed_at FROM orders o WHERE o.customer_id = $1 ORDER BY o.placed_at DESC LIMIT $2", 482_311, 1_734_500, 9_646_220, 91_200_000, 1_850_000),
                Stat(2, "reporting", "SELECT date_trunc($1, placed_at) AS day, sum(total), count(*) FROM orders WHERE placed_at >= now() - $2::interval GROUP BY 1 ORDER BY 1", 96, 612_400, 8_640, 1_210_000, 3_480_000),
                Stat(3, "app", "UPDATE orders SET status = $1, updated_at = now() WHERE id = $2", 61_027, 298_800, 61_027, 4_900_000, 38_000),
                Stat(4, "app", "SELECT c.id, c.full_name, c.email FROM customers c WHERE lower(c.email) = lower($1)", 1_203_554, 212_600, 1_203_554, 7_220_000, 4_100),
                Stat(5, "app", "INSERT INTO order_items (order_id, product_id, quantity, unit_price) VALUES ($1, $2, $3, $4)", 144_907, 96_300, 144_907, 2_310_000, 11_900),
                Stat(6, "etl", "COPY analytics.events FROM STDIN WITH (FORMAT csv)", 24, 71_900, 5_760_000, 380_000, 910_000),
                Stat(7, "reporting", "SELECT p.id, p.name, sum(oi.quantity) AS sold FROM products p JOIN order_items oi ON oi.product_id = p.id GROUP BY p.id, p.name ORDER BY sold DESC LIMIT $1", 310, 44_020, 3_100, 690_000, 240_000),
                Stat(8, "app", "SELECT id, name, price FROM products WHERE id = ANY($1)", 88_412, 18_300, 353_648, 1_420_000, 900),
            ],
            HiddenStatements: 0);
    }

    /// <summary>
    /// A LISTEN/NOTIFY feed: the JSON payloads an application's event plumbing
    /// actually publishes (which is why the monitor pretty-prints them), plus a
    /// bare-string one, since plenty of channels carry only a row id.
    /// </summary>
    public static IReadOnlyList<DatabaseNotification> Notifications()
    {
        var at = new DateTimeOffset(2026, 8, 29, 9, 41, 2, TimeSpan.Zero);

        return
        [
            new("order_events", """{"event":"order.paid","order_id":4821,"customer":"nadia.k","total":"128.40","items":[{"sku":"NIM-1","qty":2},{"sku":"NIM-7","qty":1}]}""", 4822, at),
            new("order_events", """{"event":"order.placed","order_id":4822,"customer":"tomas.r","total":"64.00","items":[{"sku":"NIM-3","qty":1}]}""", 4822, at.AddSeconds(-6)),
            new("cache_invalidation", "products:24119", 4844, at.AddSeconds(-11)),
            new("order_events", """{"event":"order.shipped","order_id":4815,"carrier":"dhl"}""", 4822, at.AddSeconds(-24)),
            new("cache_invalidation", "customers:184002", 4844, at.AddSeconds(-38)),
        ];
    }

    // --- Roles & permissions --------------------------------------------

    /// <summary>
    /// The role snapshot the security window renders: an inheritance chain
    /// (app_ro -&gt; readers), a group in the middle (writers), and a NOINHERIT
    /// membership whose privileges are dormant until SET ROLE - the case the
    /// membership tree exists to make visible.
    /// </summary>
    public static IReadOnlyList<RoleAttributes> Roles() =>
    [
        new(16390, "app_ro", true, false, true, false, false, false, false, 5,
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), ["statement_timeout=30s"], "the read-only API user"),
        new(16391, "app_rw", true, false, true, false, false, false, false, -1, null, [], null),
        new(16392, "readers", false, false, true, false, false, false, false, -1, null, [], "read-only group"),
        new(16393, "writers", false, false, true, false, false, false, false, -1, null, [], null),
        new(16394, "legacy_etl", true, false, false, false, false, false, false, -1,
            new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), [], "kept for the nightly job"),
        new(10, "postgres", true, true, true, true, true, true, true, -1, null, [], null),
    ];

    public static IReadOnlyList<RoleMembership> RoleMemberships() =>
    [
        new("app_ro", "readers", false, true, true, "postgres"),
        new("writers", "readers", false, true, true, "postgres"),
        new("app_rw", "writers", false, true, true, "postgres"),
        // Dormant until SET ROLE - shown in the tree, never counted as inherited.
        new("app_ro", "legacy_etl", true, false, true, "postgres"),
    ];

    /// <summary>
    /// A security window with its shared snapshot seeded, so the tabs render
    /// without a server. Same seam as the other fixtures: the public ViewModel
    /// surface production sets, never the views.
    /// </summary>
    public static SecurityViewModel SecurityViewModel()
    {
        var dataSource = DataSource;
        var vm = new SecurityViewModel(
            new RoleService(dataSource),
            new PrivilegeService(dataSource),
            new SecurityEditor(dataSource),
            "shop")
        {
            CurrentRole = "postgres",
            ServerVersion = new Version(17, 2),
            ServerVersionText = "PostgreSQL 17.2",
            Graph = RoleGraph.Build(Roles(), RoleMemberships()),
            Status = "5 roles · 09:41:02",
        };

        SeedRolesTab(vm);
        SeedPermissionsTab(vm);
        return vm;
    }

    /// <summary>
    /// The roles tab has no server call in its refresh — it reads the snapshot
    /// the host already holds — so the harness drives the real path rather than
    /// filling its collections behind its back.
    /// </summary>
    private static void SeedRolesTab(SecurityViewModel vm)
    {
        vm.Roles.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        vm.Roles.SelectedRole = vm.Roles.FilteredRoles.FirstOrDefault(r => r.Name == "app_ro");
    }

    /// <summary>
    /// One object's matrix, showing the thing no other client shows: SELECT that
    /// app_ro holds only through readers, two memberships up.
    /// </summary>
    private static void SeedPermissionsTab(SecurityViewModel vm)
    {
        var kinds = Privileges.For(SecurableKind.Table, new Version(17, 2));
        var columns = kinds.Select(k => new PrivilegeColumn(k, Privileges.Sql(k))).ToList();

        var objects = new[] { "orders", "order_items", "customers", "shipments" }
            .Select((name, i) => new SecurableRef(SecurableKind.Table, (uint)(16400 + i), "sales", name))
            .ToList();

        List<PermissionRowViewModel> rows =
        [
            Row("app_ro", kinds, PrivilegeSource.Inherited, "readers", PrivilegeKind.Select),
            Row("app_rw", kinds, PrivilegeSource.Inherited, "writers",
                PrivilegeKind.Select, PrivilegeKind.Insert, PrivilegeKind.Update, PrivilegeKind.Delete),
            Row("readers", kinds, PrivilegeSource.Direct, null, PrivilegeKind.Select),
            Row("legacy_etl", kinds, PrivilegeSource.None, null),
            Row("postgres", kinds, PrivilegeSource.Superuser, null),
        ];

        vm.Permissions.SeedForHarness(
            ["sales", "public", "analytics"],
            objects,
            columns,
            rows,
            "app_ro can SELECT sales.orders — inherited from readers. It cannot INSERT, UPDATE or DELETE.",
            [new ColumnGrantRow("email", "app_ro: SELECT")]);

        static PermissionRowViewModel Row(
            string role,
            IReadOnlyList<PrivilegeKind> kinds,
            PrivilegeSource source,
            string? via,
            params PrivilegeKind[] granted)
        {
            var held = new HashSet<PrivilegeKind>(granted);
            var everything = source == PrivilegeSource.Superuser;

            var cells = kinds.Select(kind =>
            {
                var isGranted = everything || held.Contains(kind);
                var effective = new EffectivePrivilege(
                    role, kind, isGranted,
                    isGranted ? source : PrivilegeSource.None,
                    isGranted ? via : null,
                    isGranted && source == PrivilegeSource.Direct ? "postgres" : null);

                // Ownership and superuser are not grants, so their cells do not toggle.
                return new PrivilegeCellViewModel(effective, !everything, _ => { });
            }).ToList();

            return new PermissionRowViewModel(role, cells);
        }
    }
}
