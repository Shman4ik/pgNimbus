using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// What pg_restore is asked to do: always all or nothing, replace only into the
/// current database, and drop the backup's owners and grants when asked.
/// </summary>
public class RestorePlanTests
{
    private const string Conn = "host=h dbname=d";

    [Test]
    public async Task Into_a_new_database_it_is_one_transaction_that_stops_at_the_first_error()
    {
        var plan = new RestorePlan("/b/shop.dump", RestoreTarget.NewDatabase, "shop_restored", KeepOwners: true);

        await Assert.That(plan.PgRestoreArguments(Conn)).IsEquivalentTo(
            ["--dbname=" + Conn, "--verbose", "--no-password", "--exit-on-error", "--single-transaction", "/b/shop.dump"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task Into_the_current_database_what_the_backup_holds_is_dropped_first()
    {
        var plan = new RestorePlan("/b/shop.dump", RestoreTarget.CurrentDatabase, "shop", KeepOwners: false);
        var arguments = plan.PgRestoreArguments(Conn);

        await Assert.That(arguments).Contains("--clean");
        await Assert.That(arguments).Contains("--if-exists");
        await Assert.That(arguments).Contains("--no-owner");
        await Assert.That(arguments).Contains("--no-privileges");
        await Assert.That(arguments).Contains("--single-transaction");
        await Assert.That(arguments[^1]).IsEqualTo("/b/shop.dump");
    }

    [Test]
    [Arguments("shop_restored", null)]
    [Arguments("Shop & Co", null)]
    [Arguments("", "Give the new database a name.")]
    [Arguments(" shop", "The name can't start or end with a space.")]
    public async Task New_database_names_are_checked_before_anything_runs(string name, string? error)
    {
        await Assert.That(RestorePlan.ValidateDatabaseName(name)).IsEqualTo(error);
    }

    [Test]
    public async Task A_name_longer_than_PostgreSQL_keeps_is_refused_and_suggestions_are_cut_to_fit()
    {
        await Assert.That(RestorePlan.ValidateDatabaseName(new string('a', 64))).Contains("too long");

        var stem = new string('s', 70);
        foreach (var candidate in RestoreService.Candidates(stem).Take(3))
        {
            await Assert.That(System.Text.Encoding.UTF8.GetByteCount(candidate)).IsLessThanOrEqualTo(RestorePlan.MaxDatabaseNameBytes);
        }

        await Assert.That(RestoreService.Candidates("shop").Take(3)).IsEquivalentTo(
            ["shop", "shop_restored", "shop_restored_2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Progress_counts_pg_restores_lines_against_the_listing()
    {
        var listing = PgArchiveListing.Parse("""
            220; 1259 16390 TABLE public customers app
            3457; 0 16390 TABLE DATA public customers app
            3305; 2606 16397 CONSTRAINT public customers customers_pkey app
            3475; 0 0 SEQUENCE SET public customers_id_seq app
            """);
        var tracker = new PgRestoreProgressTracker(listing, clean: false);

        await Assert.That(tracker.Observe("pg_restore: connecting to database for restore")).IsFalse();
        await Assert.That(tracker.Observe("pg_restore: executing SELECT pg_catalog.set_config('search_path', '', false);")).IsFalse();

        tracker.Observe("pg_restore: creating TABLE \"public.customers\"");
        await Assert.That(tracker.Stage).IsEqualTo(RestoreStage.Creating);
        await Assert.That(tracker.CurrentItem).IsEqualTo("Creating TABLE public.customers");

        tracker.Observe("pg_restore: processing data for table \"public.customers\"");
        await Assert.That(tracker.Stage).IsEqualTo(RestoreStage.RestoringData);
        await Assert.That(tracker.CurrentItem).IsEqualTo("Restoring rows of public.customers");
        await Assert.That(tracker.Fraction).IsEqualTo(0.5);

        tracker.Observe("pg_restore: executing SEQUENCE SET customers_id_seq");
        tracker.Observe("pg_restore: creating CONSTRAINT \"public.customers customers_pkey\"");
        await Assert.That(tracker.Stage).IsEqualTo(RestoreStage.Finishing);
        await Assert.That(tracker.Fraction).IsEqualTo(1d);
    }

    [Test]
    public async Task A_clean_restore_counts_the_drops_too()
    {
        var listing = PgArchiveListing.Parse("""
            220; 1259 16390 TABLE public customers app
            3457; 0 16390 TABLE DATA public customers app
            """);
        var tracker = new PgRestoreProgressTracker(listing, clean: true);

        tracker.Observe("pg_restore: dropping TABLE customers");

        await Assert.That(tracker.Stage).IsEqualTo(RestoreStage.Dropping);
        // Three steps: drop the table, create it, fill it.
        await Assert.That(tracker.Fraction).IsEqualTo(1d / 3);
    }

    [Test]
    public async Task Restore_errors_get_their_own_hints()
    {
        await Assert.That(PgToolErrorHints.For("could not execute query: ERROR:  role \"ghost\" does not exist", PgTool.PgRestore, false))
            .Contains("Keep owners and permissions");
        // A backup of the sales schema alone, whose orders point at public.customers.
        await Assert.That(PgToolErrorHints.For("could not execute query: ERROR:  relation \"public.customers\" does not exist", PgTool.PgRestore, false))
            .Contains("like the one it came from");
        await Assert.That(PgToolErrorHints.For("could not execute query: ERROR:  must be owner of extension x", PgTool.PgRestore, false))
            .Contains("Keep owners and permissions");
        await Assert.That(PgToolErrorHints.For("could not execute query: ERROR:  permission denied for schema public", PgTool.PgRestore, false))
            .Contains("new database");
    }
}
