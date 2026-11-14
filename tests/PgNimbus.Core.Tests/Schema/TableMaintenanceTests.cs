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

    [Test]
    public async Task Live_every_action_runs_against_an_odd_name_and_restrict_is_kept()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        var editor = new SchemaEditor(dataSource);
        var schema = $"Maint \"{Guid.NewGuid():N}\"";
        var quotedSchema = SqlIdentifier.Quote(schema);
        const string Table = "Odd\nTable";
        await ScalarAsync(dataSource, $"CREATE SCHEMA {quotedSchema}");
        try
        {
            await ScalarAsync(dataSource,
                $"CREATE TABLE {TableMaintenance.QualifiedName(schema, Table)} (id int PRIMARY KEY); " +
                $"INSERT INTO {TableMaintenance.QualifiedName(schema, Table)} VALUES (1), (2); " +
                $"CREATE TABLE {quotedSchema}.child (id int REFERENCES {TableMaintenance.QualifiedName(schema, Table)})");

            // VACUUM can't run in a transaction block: this proves it went alone.
            await editor.RunMaintenanceAsync(TableMaintenance.Vacuum(schema, Table), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Analyze(schema, Table), CancellationToken.None);

            // Referenced by child's foreign key: RESTRICT refuses both.
            await Assert.That(async () => await editor.RunMaintenanceAsync(TableMaintenance.Truncate(schema, Table), CancellationToken.None))
                .Throws<PostgresException>();
            await Assert.That(async () => await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, Table, RelationKind.Table), CancellationToken.None))
                .Throws<PostgresException>();
            await Assert.That(await ScalarAsync(dataSource, $"SELECT count(*) FROM {TableMaintenance.QualifiedName(schema, Table)}"))
                .IsEqualTo(2L);

            await ScalarAsync(dataSource, $"DROP TABLE {quotedSchema}.child");
            await editor.RunMaintenanceAsync(TableMaintenance.Truncate(schema, Table), CancellationToken.None);
            await Assert.That(await ScalarAsync(dataSource, $"SELECT count(*) FROM {TableMaintenance.QualifiedName(schema, Table)}"))
                .IsEqualTo(0L);

            const string Renamed = "Renamed \"T\"";
            await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, Table, RelationKind.Table, Renamed), CancellationToken.None);
            await Assert.That(await ScalarAsync(dataSource,
                $"SELECT to_regclass({SqlLiteral.Quote(TableMaintenance.QualifiedName(schema, Renamed))}) IS NOT NULL")).IsEqualTo(true);

            await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, Renamed, RelationKind.Table), CancellationToken.None);
            await Assert.That(await ScalarAsync(dataSource,
                $"SELECT to_regclass({SqlLiteral.Quote(TableMaintenance.QualifiedName(schema, Renamed))}) IS NULL")).IsEqualTo(true);

            await ScalarAsync(dataSource, $"CREATE MATERIALIZED VIEW {quotedSchema}.mv AS SELECT 1 AS x; CREATE VIEW {quotedSchema}.v AS SELECT 1 AS x");
            await editor.RunMaintenanceAsync(TableMaintenance.Vacuum(schema, "mv"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, "mv", RelationKind.MaterializedView, "mv2"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, "mv2", RelationKind.MaterializedView), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Rename(schema, "v", RelationKind.View, "v2"), CancellationToken.None);
            await editor.RunMaintenanceAsync(TableMaintenance.Drop(schema, "v2", RelationKind.View), CancellationToken.None);
        }
        finally
        {
            await ScalarAsync(dataSource, $"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }
}
