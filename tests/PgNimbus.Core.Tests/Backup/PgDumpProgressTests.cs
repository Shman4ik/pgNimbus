using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// The progress bar is read off pg_dump's own lines, so it is held to the
/// lines pg_dump 18 actually writes (captured from a real run), the errors it
/// stops with, and the friendly sentence under the common ones.
/// </summary>
public class PgDumpProgressTests
{
    private static readonly BackupTable[] Tables =
    [
        new("public", "customers", 100_000),
        new("sales", "orders", 900_000),
        new("Odd \"Schema\" *?", "Weird.Table", 0),
    ];

    [Test]
    public async Task Each_finished_table_moves_the_bar_by_its_share_of_the_data()
    {
        var tracker = new PgDumpProgressTracker(Tables, structureOnly: false);

        await Assert.That(tracker.Observe("pg_dump: reading user-defined tables")).IsFalse();
        await Assert.That(tracker.Stage).IsEqualTo(BackupStage.ReadingSchema);
        await Assert.That(tracker.Fraction).IsEqualTo(0d);

        await Assert.That(tracker.Observe("pg_dump: dumping contents of table \"Odd \"Schema\" *?.Weird.Table\"")).IsTrue();
        await Assert.That(tracker.CurrentTable).IsEqualTo("Odd \"Schema\" *?.Weird.Table");
        await Assert.That(tracker.Stage).IsEqualTo(BackupStage.SavingData);

        tracker.Observe("pg_dump: dumping contents of table \"public.customers\"");
        tracker.Observe("pg_dump: dumping contents of table \"sales.orders\"");
        await Assert.That(tracker.TablesDone).IsEqualTo(2);
        // Done: the empty table (counted as 8 KiB) and customers, of ~1 MB.
        await Assert.That(tracker.Fraction!.Value).IsBetween(0.10, 0.11);

        tracker.Finish();
        await Assert.That(tracker.TablesDone).IsEqualTo(3);
        await Assert.That(tracker.Fraction).IsEqualTo(1d);
        await Assert.That(tracker.Stage).IsEqualTo(BackupStage.Finishing);
    }

    [Test]
    public async Task A_table_the_catalog_read_did_not_know_still_counts()
    {
        var tracker = new PgDumpProgressTracker([], structureOnly: false);

        tracker.Observe("pg_dump: dumping contents of table \"public.new_one\"");
        tracker.Observe("pg_dump: dumping contents of table \"public.another\"");

        await Assert.That(tracker.TablesDone).IsEqualTo(1);
        await Assert.That(tracker.TablesTotal).IsEqualTo(2);
    }

    [Test]
    public async Task A_plain_script_finishes_its_data_when_the_post_data_definitions_start()
    {
        var tracker = new PgDumpProgressTracker(Tables[..1], structureOnly: false);

        // The pre-data definitions come first and change nothing.
        tracker.Observe("pg_dump: creating TABLE \"public.customers\"");
        await Assert.That(tracker.Stage).IsEqualTo(BackupStage.ReadingSchema);

        tracker.Observe("pg_dump: processing data for table \"public.customers\"");
        tracker.Observe("pg_dump: dumping contents of table \"public.customers\"");
        tracker.Observe("pg_dump: creating CONSTRAINT \"public.customers customers_pkey\"");

        await Assert.That(tracker.Stage).IsEqualTo(BackupStage.Finishing);
        await Assert.That(tracker.Fraction).IsEqualTo(1d);
    }

    [Test]
    public async Task A_structure_only_backup_has_no_fraction_to_show()
    {
        var tracker = new PgDumpProgressTracker(Tables, structureOnly: true);

        tracker.Observe("pg_dump: saving database definition");

        await Assert.That(tracker.Stage).IsEqualTo(BackupStage.SavingStructure);
        await Assert.That(tracker.Fraction).IsNull();
    }

    [Test]
    public async Task Errors_are_collected_with_their_detail_lines()
    {
        var log = new PgToolLog();
        log.Add("pg_dump: reading schemas");
        log.Add("pg_dump: error: query failed: ERROR:  permission denied for table secrets");
        log.Add("pg_dump: detail: Query was: LOCK TABLE public.secrets IN ACCESS SHARE MODE");
        log.Add("pg_dump: reading something else");

        await Assert.That(log.Errors.Single()).IsEqualTo(
            "query failed: ERROR:  permission denied for table secrets\ndetail: Query was: LOCK TABLE public.secrets IN ACCESS SHARE MODE");
        await Assert.That(log.Text).Contains("reading schemas");
    }

    [Test]
    public async Task The_log_keeps_the_latest_lines_and_says_how_many_it_dropped()
    {
        var log = new PgToolLog();
        for (var i = 0; i < PgToolLog.MaxLines + 10; i++)
        {
            log.Add($"pg_dump: line {i}");
        }

        await Assert.That(log.Text).StartsWith("(10 earlier lines not kept)");
        await Assert.That(log.Text).EndsWith($"line {PgToolLog.MaxLines + 9}");
    }

    [Test]
    public async Task Familiar_errors_get_a_sentence_saying_what_to_do()
    {
        await Assert.That(PgToolErrorHints.For(
            "connection to server at \"127.0.0.1\", port 5443 failed: FATAL:  password authentication failed for user \"nobody\"",
            PgTool.PgDump,
            tunnelled: false)).Contains("password");
        await Assert.That(PgToolErrorHints.For("could not connect to server: Connection refused", PgTool.PgDump, tunnelled: true)).Contains("SSH tunnel");
        await Assert.That(PgToolErrorHints.For("query failed: ERROR:  permission denied for table x", PgTool.PgDump, false)).Contains("role");
        await Assert.That(PgToolErrorHints.For("something nobody has seen", PgTool.PgDump, false)).IsNull();
    }
}
