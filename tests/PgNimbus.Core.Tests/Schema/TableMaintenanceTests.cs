using Npgsql;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// The statements behind a relation's Maintenance submenu. The offline half
/// pins the text each action sends, odd names included (judged with
/// <see cref="SqlScriptSplitter"/>: an escaped quote that let a name out would
/// add a statement); the live half, gated on <c>PGNIMBUS_TEST_CONN</c>, runs
/// them through <see cref="SchemaEditor"/> against a real server.
/// </summary>
[NotInParallel]
public class TableMaintenanceTests
{
    // A quote, uppercase, a newline and a would-be second statement.
    private const string OddSchema = "Sales \"EU\"";
    private const string OddTable = "Orders\n; DROP TABLE x; --";

    [Test]
    public async Task Each_action_sends_its_plain_statement()
    {
        await Assert.That(TableMaintenance.For(TableMaintenanceAction.Analyze, "public", "orders", RelationKind.Table))
            .IsEqualTo("ANALYZE \"public\".\"orders\"");
        await Assert.That(TableMaintenance.For(TableMaintenanceAction.Vacuum, "public", "orders", RelationKind.Table))
            .IsEqualTo("VACUUM \"public\".\"orders\"");
        await Assert.That(TableMaintenance.For(TableMaintenanceAction.Truncate, "public", "orders", RelationKind.Table))
            .IsEqualTo("TRUNCATE TABLE \"public\".\"orders\"");
        await Assert.That(TableMaintenance.For(TableMaintenanceAction.Rename, "public", "orders", RelationKind.Table, "Orders 2"))
            .IsEqualTo("ALTER TABLE \"public\".\"orders\" RENAME TO \"Orders 2\"");
        await Assert.That(TableMaintenance.For(TableMaintenanceAction.Drop, "public", "orders", RelationKind.PartitionedTable))
            .IsEqualTo("DROP TABLE \"public\".\"orders\"");
    }

    [Test]
    public async Task Rename_and_drop_take_the_keyword_of_the_relation_kind()
    {
        await Assert.That(TableMaintenance.Drop("public", "v", RelationKind.View)).IsEqualTo("DROP VIEW \"public\".\"v\"");
        await Assert.That(TableMaintenance.Drop("public", "m", RelationKind.MaterializedView))
            .IsEqualTo("DROP MATERIALIZED VIEW \"public\".\"m\"");
        await Assert.That(TableMaintenance.Rename("public", "m", RelationKind.MaterializedView, "n"))
            .IsEqualTo("ALTER MATERIALIZED VIEW \"public\".\"m\" RENAME TO \"n\"");
        await Assert.That(TableMaintenance.Rename("public", "v", RelationKind.View, "w"))
            .IsEqualTo("ALTER VIEW \"public\".\"v\" RENAME TO \"w\"");
    }

    [Test]
    public async Task Odd_names_stay_one_quoted_identifier_each()
    {
        foreach (var action in Enum.GetValues<TableMaintenanceAction>())
        {
            var sql = TableMaintenance.For(action, OddSchema, OddTable, RelationKind.Table, "New\"Name\n--");

            await Assert.That(SqlScriptSplitter.Split(sql).Count).IsEqualTo(1);
            await Assert.That(sql).Contains("\"Sales \"\"EU\"\"\".\"Orders\n; DROP TABLE x; --\"");
            await Assert.That(sql).DoesNotContain(InternalSql.Marker);
        }

        await Assert.That(TableMaintenance.Rename("s", "t", RelationKind.Table, "New\"Name\n--"))
            .EndsWith("RENAME TO \"New\"\"Name\n--\"");
    }

    [Test]
    public async Task Each_action_is_offered_only_where_postgres_accepts_it()
    {
        foreach (var kind in new[] { RelationKind.Table, RelationKind.PartitionedTable })
        {
            foreach (var action in Enum.GetValues<TableMaintenanceAction>())
            {
                await Assert.That(TableMaintenance.Applies(action, kind)).IsTrue();
            }
        }

        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Analyze, RelationKind.MaterializedView)).IsTrue();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Vacuum, RelationKind.MaterializedView)).IsTrue();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Truncate, RelationKind.MaterializedView)).IsFalse();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Rename, RelationKind.MaterializedView)).IsTrue();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Drop, RelationKind.MaterializedView)).IsTrue();

        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Analyze, RelationKind.View)).IsFalse();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Vacuum, RelationKind.View)).IsFalse();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Truncate, RelationKind.View)).IsFalse();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Rename, RelationKind.View)).IsTrue();
        await Assert.That(TableMaintenance.Applies(TableMaintenanceAction.Drop, RelationKind.View)).IsTrue();
    }

    [Test]
    public async Task A_new_name_the_server_would_cut_short_or_cannot_take_is_refused()
    {
        await Assert.That(TableMaintenance.CheckNewName("orders_2024")).IsNull();
        await Assert.That(TableMaintenance.CheckNewName(new string('a', 63))).IsNull();
        await Assert.That(TableMaintenance.CheckNewName("  ")).IsNotNull();
        await Assert.That(TableMaintenance.CheckNewName(new string('a', 64))).IsNotNull();
        // 32 two-byte characters: 32 characters, 64 bytes.
        await Assert.That(TableMaintenance.CheckNewName(new string('é', 32))).IsNotNull();
        await Assert.That(TableMaintenance.CheckNewName("a\0b")).IsNotNull();
    }

    // --- Live (PGNIMBUS_TEST_CONN) -------------------------------------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to run maintenance against.");
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    private static Task<object?> CountAsync(NpgsqlDataSource dataSource, string schema, string table) =>
        ScalarAsync(dataSource, $"SELECT count(*) FROM {TableMaintenance.QualifiedName(schema, table)}");

    private static async Task<bool> ExistsAsync(NpgsqlDataSource dataSource, string schema, string table) =>
        (bool)(await ScalarAsync(dataSource,
            $"SELECT to_regclass({SqlLiteral.Quote(TableMaintenance.QualifiedName(schema, table))}) IS NOT NULL"))!;

    // The last time ANALYZE or VACUUM touched a relation, from the cumulative
    // statistics (flushed when the maintenance command ends).
    private static async Task<bool> StatSetAsync(NpgsqlDataSource dataSource, string column, string schema, string table)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await ScalarAsync(dataSource, "SELECT pg_stat_clear_snapshot()");
            var value = await ScalarAsync(dataSource,
                $"SELECT {column} IS NOT NULL FROM pg_stat_user_tables WHERE schemaname = {SqlLiteral.Quote(schema)} AND relname = {SqlLiteral.Quote(table)}");
            if (value is true)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<(NpgsqlDataSource DataSource, string Schema)> LiveSchemaAsync()
    {
        var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var schema = $"Maint \"{Guid.NewGuid():N}\"";
        await ScalarAsync(dataSource, $"CREATE SCHEMA {SqlIdentifier.Quote(schema)}");
        return (dataSource, schema);
    }

    private static async Task DropLiveSchemaAsync(NpgsqlDataSource dataSource, string schema)
    {
        await ScalarAsync(dataSource, $"DROP SCHEMA {SqlIdentifier.Quote(schema)} CASCADE");
        await dataSource.DisposeAsync();
    }

    /// <summary>
    /// Every action on a name with quotes, uppercase, a space and a newline,
    /// while a table of the very same name sits in <c>public</c>, on the
    /// search_path: only the qualified one is touched.
    /// </summary>
    [Test]
    public async Task Live_each_action_touches_only_the_odd_named_table_it_names()
    {
        SkipIfNoConnection();
        var (dataSource, schema) = await LiveSchemaAsync();
        var table = $"Odd \"T\"\nable {Guid.NewGuid().ToString("N")[..8]}";
        var decoy = TableMaintenance.QualifiedName("public", table);
        var editor = new SchemaEditor(dataSource);
        try
        {
            await ScalarAsync(dataSource,
                $"CREATE TABLE {TableMaintenance.QualifiedName(schema, table)} (id int); " +
                $"INSERT INTO {TableMaintenance.QualifiedName(schema, table)} VALUES (1), (2); " +
                $"CREATE TABLE {decoy} (id int); INSERT INTO {decoy} VALUES (1), (2), (3)");

            await editor.RunMaintenanceAsync(TableMaintenance.Analyze(schema, table), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Vacuum(schema, table), CancellationToken.None);
            await Assert.That(await StatSetAsync(dataSource, "last_analyze", schema, table)).IsTrue();
            await Assert.That(await StatSetAsync(dataSource, "last_vacuum", schema, table)).IsTrue();
            await Assert.That(await ScalarAsync(dataSource,
                $"SELECT last_analyze IS NULL AND last_vacuum IS NULL FROM pg_stat_user_tables WHERE schemaname = 'public' AND relname = {SqlLiteral.Quote(table)}"))
                .IsEqualTo(true);

            await editor.RunMaintenanceAsync(TableMaintenance.Truncate(schema, table), CancellationToken.None);
            await Assert.That(await CountAsync(dataSource, schema, table)).IsEqualTo(0L);
            await Assert.That(await CountAsync(dataSource, "public", table)).IsEqualTo(3L);

            var renamed = table + " Renamed";
            await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, table, RelationKind.Table, renamed), CancellationToken.None);
            await Assert.That(await ExistsAsync(dataSource, schema, renamed)).IsTrue();
            await Assert.That(await ExistsAsync(dataSource, schema, table)).IsFalse();
            await Assert.That(await ExistsAsync(dataSource, "public", table)).IsTrue();

            await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, renamed, RelationKind.Table), CancellationToken.None);
            await Assert.That(await ExistsAsync(dataSource, schema, renamed)).IsFalse();
            await Assert.That(await CountAsync(dataSource, "public", table)).IsEqualTo(3L);
        }
        finally
        {
            await ScalarAsync(dataSource, $"DROP TABLE IF EXISTS {decoy}");
            await DropLiveSchemaAsync(dataSource, schema);
        }
    }

    [Test]
    public async Task Live_a_partitioned_parent_is_analyzed_vacuumed_truncated_and_dropped_with_its_partitions()
    {
        SkipIfNoConnection();
        var (dataSource, schema) = await LiveSchemaAsync();
        var s = SqlIdentifier.Quote(schema);
        var editor = new SchemaEditor(dataSource);
        try
        {
            await ScalarAsync(dataSource,
                $"CREATE TABLE {s}.\"Parent\" (id int) PARTITION BY RANGE (id); " +
                $"CREATE TABLE {s}.p1 PARTITION OF {s}.\"Parent\" FOR VALUES FROM (0) TO (10); " +
                $"CREATE TABLE {s}.p2 PARTITION OF {s}.\"Parent\" FOR VALUES FROM (10) TO (20); " +
                $"INSERT INTO {s}.\"Parent\" VALUES (1), (15)");

            await editor.RunMaintenanceAsync(TableMaintenance.Analyze(schema, "Parent"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Vacuum(schema, "Parent"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Truncate(schema, "Parent"), CancellationToken.None);
            await Assert.That(await CountAsync(dataSource, schema, "p1")).IsEqualTo(0L);
            await Assert.That(await CountAsync(dataSource, schema, "p2")).IsEqualTo(0L);

            await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, "Parent", RelationKind.PartitionedTable, "Parent2"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, "Parent2", RelationKind.PartitionedTable), CancellationToken.None);
            await Assert.That(await ExistsAsync(dataSource, schema, "Parent2")).IsFalse();
            await Assert.That(await ExistsAsync(dataSource, schema, "p1")).IsFalse();
            await Assert.That(await ExistsAsync(dataSource, schema, "p2")).IsFalse();
        }
        finally
        {
            await DropLiveSchemaAsync(dataSource, schema);
        }
    }

    [Test]
    public async Task Live_a_referenced_table_is_refused_truncate_and_drop_and_left_intact()
    {
        SkipIfNoConnection();
        var (dataSource, schema) = await LiveSchemaAsync();
        var s = SqlIdentifier.Quote(schema);
        var editor = new SchemaEditor(dataSource);
        try
        {
            await ScalarAsync(dataSource,
                $"CREATE TABLE {s}.parent (id int PRIMARY KEY); INSERT INTO {s}.parent VALUES (1), (2); " +
                $"CREATE TABLE {s}.child (id int REFERENCES {s}.parent)");

            var truncate = await Assert.That(async () => await editor.RunMaintenanceAsync(TableMaintenance.Truncate(schema, "parent"), CancellationToken.None))
                .Throws<PostgresException>();
            await Assert.That(truncate!.SqlState).IsEqualTo(PostgresErrorCodes.FeatureNotSupported);
            await Assert.That(truncate.Message).Contains("foreign key");

            var drop = await Assert.That(async () => await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, "parent", RelationKind.Table), CancellationToken.None))
                .Throws<PostgresException>();
            await Assert.That(drop!.SqlState).IsEqualTo(PostgresErrorCodes.DependentObjectsStillExist);
            await Assert.That(drop.Message).Contains("depend");

            await Assert.That(await CountAsync(dataSource, schema, "parent")).IsEqualTo(2L);
        }
        finally
        {
            await DropLiveSchemaAsync(dataSource, schema);
        }
    }

    [Test]
    public async Task Live_views_and_materialized_views_take_their_own_keyword()
    {
        SkipIfNoConnection();
        var (dataSource, schema) = await LiveSchemaAsync();
        var s = SqlIdentifier.Quote(schema);
        var editor = new SchemaEditor(dataSource);
        try
        {
            await ScalarAsync(dataSource, $"CREATE MATERIALIZED VIEW {s}.\"Mv\" AS SELECT 1 AS x; CREATE VIEW {s}.\"V w\" AS SELECT 1 AS x");

            await editor.RunMaintenanceAsync(TableMaintenance.Analyze(schema, "Mv"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Vacuum(schema, "Mv"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, "Mv", RelationKind.MaterializedView, "Mv2"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, "Mv2", RelationKind.MaterializedView), CancellationToken.None);
            await Assert.That(await ExistsAsync(dataSource, schema, "Mv2")).IsFalse();

            await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, "V w", RelationKind.View, "V w2"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, "V w2", RelationKind.View), CancellationToken.None);
            await Assert.That(await ExistsAsync(dataSource, schema, "V w2")).IsFalse();
        }
        finally
        {
            await DropLiveSchemaAsync(dataSource, schema);
        }
    }

    [Test]
    public async Task Live_a_rename_to_a_taken_name_is_refused_by_the_server()
    {
        SkipIfNoConnection();
        var (dataSource, schema) = await LiveSchemaAsync();
        var s = SqlIdentifier.Quote(schema);
        var editor = new SchemaEditor(dataSource);
        try
        {
            await ScalarAsync(dataSource, $"CREATE TABLE {s}.a (id int); CREATE TABLE {s}.b (id int)");

            var taken = await Assert.That(async () => await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, "a", RelationKind.Table, "b"), CancellationToken.None))
                .Throws<PostgresException>();
            await Assert.That(taken!.SqlState).IsEqualTo(PostgresErrorCodes.DuplicateTable);
            await Assert.That(await ExistsAsync(dataSource, schema, "a")).IsTrue();
        }
        finally
        {
            await DropLiveSchemaAsync(dataSource, schema);
        }
    }

    /// <summary>
    /// VACUUM can't run inside a transaction block. With the window's explicit
    /// transaction open on the same data source, the editor still vacuums, on a
    /// pooled connection of its own.
    /// </summary>
    [Test]
    public async Task Live_vacuum_runs_on_its_own_connection_while_an_explicit_transaction_is_open()
    {
        SkipIfNoConnection();
        var (dataSource, schema) = await LiveSchemaAsync();
        var s = SqlIdentifier.Quote(schema);
        var editor = new SchemaEditor(dataSource);
        var engine = new QueryEngine(dataSource);
        try
        {
            await ScalarAsync(dataSource, $"CREATE TABLE {s}.t (id int); INSERT INTO {s}.t VALUES (1)");
            await engine.BeginTransactionAsync(CancellationToken.None);
            await Assert.That(engine.IsInTransaction).IsTrue();

            await editor.RunMaintenanceAsync(TableMaintenance.Vacuum(schema, "t"), CancellationToken.None);

            await Assert.That(await StatSetAsync(dataSource, "last_vacuum", schema, "t")).IsTrue();
            await engine.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            if (engine.IsInTransaction)
            {
                await engine.RollbackAsync(CancellationToken.None);
            }

            await DropLiveSchemaAsync(dataSource, schema);
        }
    }
}
