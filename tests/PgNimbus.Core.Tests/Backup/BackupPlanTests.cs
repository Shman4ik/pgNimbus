using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// What pg_dump is asked for: the right part of the database, the right
/// format, written where a failure can't destroy an older backup, and names
/// matched exactly, so a schema called <c>a*</c> never brings <c>ab</c> along.
/// </summary>
public class BackupPlanTests
{
    private const string Conn = "host=h dbname=d";

    [Test]
    public async Task A_whole_database_backup_is_a_custom_archive_written_to_the_partial_file()
    {
        var plan = new BackupPlan(BackupScope.Database, BackupContent.Everything, "/b/shop.dump");

        await Assert.That(plan.Format).IsEqualTo(BackupFormat.Archive);
        await Assert.That(plan.PgDumpArguments(new PgVersion(18, 6), Conn)).IsEquivalentTo(
            ["--format=custom", "--file=/b/shop.dump.partial", "--verbose", "--no-password", Conn],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_sql_file_is_a_plain_script_and_structure_only_skips_the_rows()
    {
        var plan = new BackupPlan(BackupScope.Database, BackupContent.StructureOnly, "/b/shop.SQL");

        await Assert.That(plan.Format).IsEqualTo(BackupFormat.SqlScript);
        var arguments = plan.PgDumpArguments(new PgVersion(18, 6), Conn);
        await Assert.That(arguments).Contains("--format=plain");
        await Assert.That(arguments).Contains("--schema-only");
    }

    [Test]
    public async Task Schemas_and_tables_are_matched_by_their_exact_names()
    {
        var schema = new BackupPlan(BackupScope.ForSchema("Odd \"Schema\" *?"), BackupContent.Everything, "/b/x.dump");
        await Assert.That(schema.PgDumpArguments(new PgVersion(18, 0), Conn)).Contains("--schema=\"Odd \"\"Schema\"\" *?\"");

        var table = new BackupPlan(BackupScope.ForTable("sales", "Weird.Table", isPartitioned: false), BackupContent.Everything, "/b/x.dump");
        await Assert.That(table.PgDumpArguments(new PgVersion(18, 0), Conn)).Contains("--table=\"sales\".\"Weird.Table\"");
    }

    [Test]
    public async Task A_partitioned_table_brings_its_partitions()
    {
        var plan = new BackupPlan(BackupScope.ForTable("sales", "events", isPartitioned: true), BackupContent.Everything, "/b/x.dump");

        await Assert.That(plan.PgDumpArguments(new PgVersion(16, 0), Conn)).Contains("--table-and-children=\"sales\".\"events\"");

        // pg_dump 15 has no --table-and-children: the partitions are named one by one.
        var old = plan.PgDumpArguments(new PgVersion(15, 4), Conn, [new BackupTable("sales", "events_2026", 100)]);
        await Assert.That(old).Contains("--table=\"sales\".\"events\"");
        await Assert.That(old).Contains("--table=\"sales\".\"events_2026\"");
    }

    [Test]
    public async Task The_suggested_name_carries_what_and_when_and_nothing_a_file_system_refuses()
    {
        var now = new DateTime(2026, 10, 10, 14, 32, 0);

        await Assert.That(BackupScope.Database.SuggestedFileName("shop", now)).IsEqualTo("shop_2026-10-10_1432");
        await Assert.That(BackupScope.ForTable("sales", "orders", false).SuggestedFileName("shop", now)).IsEqualTo("shop_sales.orders_2026-10-10_1432");
        await Assert.That(BackupScope.ForSchema("a/b:c*?\"").SuggestedFileName("shop", now)).IsEqualTo("shop_a_b_c____2026-10-10_1432");
    }

    [Test]
    public async Task Scopes_describe_themselves_for_the_window_title()
    {
        await Assert.That(BackupScope.Database.Describe("shop")).IsEqualTo("shop");
        await Assert.That(BackupScope.ForSchema("sales").Describe("shop")).IsEqualTo("schema sales");
        await Assert.That(BackupScope.ForTable("sales", "orders", false).Describe("shop")).IsEqualTo("table sales.orders");
    }
}
