using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Tests;

/// <summary>
/// Security audit 2026-09, finding 6: a non-safe-mode multi-row delete used to
/// run one autocommit DELETE per row, so a failure partway through (a
/// blocking trigger, a lost connection) left whatever had already committed
/// deleted and the rest untouched. <see cref="QueryViewModel.DeleteRowsAsync"/>
/// now builds one <see cref="ParameterizedStatement"/> per row and hands the
/// list to <see cref="QueryEngine.ApplyBatchAsync(IReadOnlyList{ParameterizedStatement}, CancellationToken)"/>,
/// which runs the whole batch inside one transaction — a mid-batch failure
/// rolls all of it back. Gated on <c>PGNIMBUS_TEST_CONN</c>, in the style of
/// <c>ResultExportTests</c>' server-backed half.
/// </summary>
[NotInParallel]
public class QueryViewModelDeleteRowsTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private const string Table = "pgnimbus_delete_scratch";

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to delete against.");
        }
    }

    // A BEFORE DELETE trigger that only ever blocks the row named "2" — the
    // "clean delete" test seeds ids that never trip it, so one table/trigger
    // pair serves both tests.
    private static async Task SeedTableAsync(NpgsqlDataSource dataSource, IReadOnlyList<int> ids)
    {
        var values = string.Join(", ", ids.Select(id => $"({id}, 'row {id}')"));
        await using var command = dataSource.CreateCommand(
            $"""
            DROP TABLE IF EXISTS public.{Table} CASCADE;
            CREATE TABLE public.{Table} (id int PRIMARY KEY, name text);
            INSERT INTO public.{Table} (id, name) VALUES {values};

            CREATE OR REPLACE FUNCTION pgnimbus_delete_scratch_guard() RETURNS trigger AS $$
            BEGIN
                IF OLD.id = 2 THEN
                    RAISE EXCEPTION 'blocked by trigger';
                END IF;
                RETURN OLD;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER pgnimbus_delete_scratch_guard_trigger
            BEFORE DELETE ON public.{Table}
            FOR EACH ROW EXECUTE FUNCTION pgnimbus_delete_scratch_guard();
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropTableAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand($"DROP TABLE IF EXISTS public.{Table} CASCADE");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand($"SELECT count(*) FROM public.{Table}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // A plain query tab, not a browsed one: browsing reloads the page after
    // any delete (right or wrong), and that reload runs the ordinary query
    // pipeline, which always finishes by setting Status to "Done" — clobbering
    // whatever DeleteRowsAsync just said. A plain query tab has no such
    // reload, so the delete's own status message ("Deleted N rows" / "Delete
    // failed, nothing deleted: …") is what's left to read.
    private static async Task<QueryViewModel> OpenEditableQueryTabAsync(NpgsqlDataSource dataSource)
    {
        var schema = new SchemaService(dataSource);
        var tab = new QueryViewModel(new QueryEngine(dataSource), new ExplainService(dataSource), schemaService: schema)
        {
            Sql = $"SELECT id, name FROM public.{Table} ORDER BY id",
        };
        await tab.RunCommand.ExecuteAsync(null);
        if (!tab.IsEditable)
        {
            throw new InvalidOperationException($"Expected the query to establish an edit context; status was \"{tab.Status}\".");
        }

        return tab;
    }

    [Test]
    public async Task A_trigger_blocking_the_second_row_leaves_all_three_undeleted()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await SeedTableAsync(dataSource, [1, 2, 3]);
        try
        {
            await Ui.Run(async () =>
            {
                var tab = await OpenEditableQueryTabAsync(dataSource);
                await Assert.That(tab.Rows.Count).IsEqualTo(3);

                var deleted = await tab.DeleteRowsAsync(tab.Rows.ToList());

                await Assert.That(deleted).IsEqualTo(0);
                await Assert.That(tab.HasError).IsTrue();
                await Assert.That(tab.Status).StartsWith("Delete failed, nothing deleted: ");
                await Assert.That(tab.Status).Contains("blocked by trigger");
            });

            await Assert.That(await CountRowsAsync(dataSource)).IsEqualTo(3L);
        }
        finally
        {
            await DropTableAsync(dataSource);
        }
    }

    [Test]
    public async Task A_clean_delete_removes_every_row()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        // None of these ids is 2, so the same guard trigger never fires.
        await SeedTableAsync(dataSource, [11, 12, 13]);
        try
        {
            await Ui.Run(async () =>
            {
                var tab = await OpenEditableQueryTabAsync(dataSource);
                await Assert.That(tab.Rows.Count).IsEqualTo(3);

                var deleted = await tab.DeleteRowsAsync(tab.Rows.ToList());

                await Assert.That(deleted).IsEqualTo(3);
                await Assert.That(tab.HasError).IsFalse();
                await Assert.That(tab.Status).IsEqualTo("Deleted 3 rows");
            });

            await Assert.That(await CountRowsAsync(dataSource)).IsEqualTo(0L);
        }
        finally
        {
            await DropTableAsync(dataSource);
        }
    }
}
