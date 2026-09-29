using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Tests;

/// <summary>
/// An edit context names a table, and it must only ever be handed out over
/// that table's own rows (security audit 2026-09, finding 14). Two ways it
/// wasn't: a hand-edited browse page query naming the table bare, which
/// <c>search_path</c> resolved to a same-named table in another schema, and a
/// self-join, whose columns all carry the one table's OID. Both need a real
/// server: the wire metadata is what the checks read.
/// </summary>
[NotInParallel]
public class BrowseEditTargetTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    // The browsed schema, and the one search_path finds a bare "orders" in.
    private const string Browsed = "pgnimbus_edit_target_browsed";
    private const string Other = "pgnimbus_edit_target_other";

    private static async Task<NpgsqlDataSource> SeedAsync()
    {
        // search_path puts the *other* schema first, as a schema-per-tenant or
        // staging/prod setup would for a session that isn't the browsed one's.
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = Other };
        var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var seed = dataSource.CreateCommand($"""
            DROP SCHEMA IF EXISTS {Browsed} CASCADE;
            DROP SCHEMA IF EXISTS {Other} CASCADE;
            CREATE SCHEMA {Browsed};
            CREATE SCHEMA {Other};
            CREATE TABLE {Browsed}.orders (id int PRIMARY KEY, name text);
            CREATE TABLE {Other}.orders (id int PRIMARY KEY, name text);
            INSERT INTO {Browsed}.orders VALUES (1, 'browsed one'), (2, 'browsed two');
            INSERT INTO {Other}.orders VALUES (1, 'other one'), (2, 'other two');
            CREATE TABLE {Browsed}.items (id int PRIMARY KEY, parent_id int, name text);
            INSERT INTO {Browsed}.items VALUES (1, NULL, 'root'), (2, 1, 'child');
            """);
        await seed.ExecuteNonQueryAsync();
        return dataSource;
    }

    private static async Task DropAsync(NpgsqlDataSource dataSource)
    {
        await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {Browsed} CASCADE; DROP SCHEMA IF EXISTS {Other} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }

    private static QueryViewModel NewTab(NpgsqlDataSource dataSource, SchemaService schema) =>
        new(new QueryEngine(dataSource), new ExplainService(dataSource), schemaService: schema);

    [Test]
    public async Task A_browse_query_that_finds_another_table_by_search_path_is_not_resumed_or_editable()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to browse.");
        }

        await using var dataSource = await SeedAsync();
        try
        {
            await Ui.Run(async () =>
            {
                var schema = new SchemaService(dataSource);
                var tab = NewTab(dataSource, schema);

                await tab.StartBrowseAsync(Browsed, "orders", await schema.GetColumnsAsync(Browsed, "orders", CancellationToken.None));
                await Assert.That(tab.IsBrowsing).IsTrue();
                await Assert.That(tab.EditContext?.Schema).IsEqualTo(Browsed);

                // The page query edited by hand to name the table bare: still the
                // browse shape, but search_path finds the other schema's orders.
                tab.Sql = "SELECT * FROM orders LIMIT 100";
                await tab.RunCommand.ExecuteAsync(null);

                await Assert.That(tab.HasError).IsFalse();
                await Assert.That(tab.Rows[0][1]).IsEqualTo("other one");
                await Assert.That(tab.IsBrowsing).IsFalse();
                await Assert.That(tab.ShowsBrowseChrome).IsFalse();
                await Assert.That(tab.EditContext).IsNull();
                await Assert.That(tab.IsEditable).IsFalse();
                await Assert.That(tab.ReadOnlyHint!).Contains($"doesn't read {Browsed}.orders");
                await Assert.That(tab.Status).Contains($"doesn't read {Browsed}.orders");

                // Naming the browsed table in full still resumes browse mode, chips
                // and editing included: the fix refuses only the other table.
                tab.Sql = $"SELECT * FROM {Browsed}.orders WHERE id = 2 LIMIT 100";
                await tab.RunCommand.ExecuteAsync(null);

                await Assert.That(tab.Rows[0][1]).IsEqualTo("browsed two");
                await Assert.That(tab.IsBrowsing).IsTrue();
                await Assert.That(tab.EditContext?.Schema).IsEqualTo(Browsed);
            });
        }
        finally
        {
            await DropAsync(dataSource);
        }
    }

    [Test]
    public async Task A_restored_browse_tab_learns_its_table_by_exact_name_before_its_first_run()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to browse.");
        }

        await using var dataSource = await SeedAsync();
        try
        {
            await Ui.Run(async () =>
            {
                var schema = new SchemaService(dataSource);

                // No page has run in this session, so the OID can't come from one.
                var tab = NewTab(dataSource, schema);
                tab.RestoreBrowsedTable(Browsed, "orders");
                tab.Sql = "SELECT * FROM orders LIMIT 100";
                await tab.RunCommand.ExecuteAsync(null);

                await Assert.That(tab.Rows[0][1]).IsEqualTo("other one");
                await Assert.That(tab.IsBrowsing).IsFalse();
                await Assert.That(tab.EditContext).IsNull();

                var resumed = NewTab(dataSource, schema);
                resumed.RestoreBrowsedTable(Browsed, "orders");
                resumed.Sql = $"SELECT * FROM {Browsed}.orders LIMIT 100";
                await resumed.RunCommand.ExecuteAsync(null);

                await Assert.That(resumed.IsBrowsing).IsTrue();
                await Assert.That(resumed.EditContext?.Schema).IsEqualTo(Browsed);
            });
        }
        finally
        {
            await DropAsync(dataSource);
        }
    }

    [Test]
    public async Task A_self_join_is_read_only_and_a_plain_read_of_the_same_table_is_not()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to query.");
        }

        await using var dataSource = await SeedAsync();
        try
        {
            await Ui.Run(async () =>
            {
                var tab = NewTab(dataSource, new SchemaService(dataSource));

                // name is the parent's: an edit of it through an edit context on
                // items would have updated the child row.
                tab.Sql = $"SELECT c.id, p.name FROM {Browsed}.items c JOIN {Browsed}.items p ON p.id = c.parent_id";
                await tab.RunCommand.ExecuteAsync(null);

                await Assert.That(tab.Rows.Count).IsEqualTo(1);
                await Assert.That(tab.EditContext).IsNull();
                await Assert.That(tab.ReadOnlyHint!).Contains("self-join");

                tab.Sql = $"SELECT id, name FROM {Browsed}.items";
                await tab.RunCommand.ExecuteAsync(null);

                await Assert.That(tab.EditContext?.Table).IsEqualTo("items");
            });
        }
        finally
        {
            await DropAsync(dataSource);
        }
    }
}
