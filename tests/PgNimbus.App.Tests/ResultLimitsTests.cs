using Avalonia.Controls;
using Avalonia.VisualTree;
using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// What a result may cost the client (security audit 2026-09, finding 16). Rows
/// were the only bound: 100,000 rows of anything, per statement of a script, in
/// as many grid columns as the server claimed. Now a result also stops at
/// <see cref="QueryViewModel.MaxDisplayBytes"/>, a script's sections share one
/// budget, and the grid builds at most <see cref="QueryViewModel.MaxGridColumns"/>.
/// The first tests feed batches from memory; the last, gated on
/// <c>PGNIMBUS_TEST_CONN</c>, trips the byte cap on a real server.
/// </summary>
[NotInParallel]
public class ResultLimitsTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    [Test]
    public async Task Collecting_stops_at_the_byte_budget_and_stops_reading()
    {
        var produced = new ProducedBatches(batches: 50, rowsPerBatch: 10, textLength: 50_000);
        var rows = new List<object?[]>();
        var afterBatch = 0;

        // Each row is ~100 KB as UTF-16; a 950 KB budget holds nine of them.
        var cap = await QueryViewModel.CollectRowsAsync(
            produced.Read(), rows, new ResultBudget(1_000, 950_000), () => { afterBatch++; return Task.CompletedTask; }, CancellationToken.None);

        await Assert.That(cap).IsEqualTo(ResultCap.Bytes);
        await Assert.That(rows.Count).IsEqualTo(10 - 1);
        // The partial batch still reports, and nothing past it was pulled: leaving
        // the loop disposed the stream (which is what makes the engine cancel).
        await Assert.That(afterBatch).IsEqualTo(1);
        await Assert.That(produced.Yielded).IsEqualTo(1);
        await Assert.That(produced.Disposed).IsTrue();
    }

    [Test]
    public async Task Collecting_stops_at_the_row_cap_the_same_way()
    {
        var produced = new ProducedBatches(batches: 50, rowsPerBatch: 200, textLength: 1);
        var rows = new List<object?[]>();

        var cap = await QueryViewModel.CollectRowsAsync(
            produced.Read(), rows, new ResultBudget(1_000, QueryViewModel.MaxDisplayBytes), () => Task.CompletedTask, CancellationToken.None);

        await Assert.That(cap).IsEqualTo(ResultCap.Rows);
        await Assert.That(rows.Count).IsEqualTo(1_000);
        await Assert.That(produced.Yielded).IsEqualTo(6);
        await Assert.That(produced.Disposed).IsTrue();
    }

    [Test]
    public async Task A_result_read_to_the_end_is_not_capped()
    {
        var produced = new ProducedBatches(batches: 3, rowsPerBatch: 5, textLength: 10);
        var rows = new List<object?[]>();

        var cap = await QueryViewModel.CollectRowsAsync(
            produced.Read(), rows, new ResultBudget(1_000, QueryViewModel.MaxDisplayBytes), () => Task.CompletedTask, CancellationToken.None);

        await Assert.That(cap).IsEqualTo(ResultCap.None);
        await Assert.That(rows.Count).IsEqualTo(15);
        await Assert.That(QueryViewModel.CapTextFor(cap)).IsNull();
    }

    [Test]
    public async Task Each_cap_says_which_limit_it_was()
    {
        using var invariant = InvariantCulture.Scope();
        await Assert.That(QueryViewModel.CapTextFor(ResultCap.Rows)).StartsWith("capped at 100,000 rows");
        await Assert.That(QueryViewModel.CapTextFor(ResultCap.Bytes)).StartsWith("capped at 256 MB");
        await Assert.That(QueryViewModel.CapTextFor(ResultCap.Shared)).Contains("the script's results reached");

        var section = ScriptResultViewModel.From(2, "SELECT big FROM t", new MaterializedResultSet
        {
            Elapsed = TimeSpan.FromMilliseconds(1),
            Columns = [new ColumnInfo("big", "text", typeof(string))],
            Rows = [["x"]],
            Truncated = true,
            CappedBy = ResultCap.Shared,
        });
        await Assert.That(section.CapText).IsEqualTo(QueryViewModel.CapTextFor(ResultCap.Shared));
    }

    [Test]
    public async Task The_grid_builds_at_most_a_thousand_columns_and_says_so()
    {
        // A server can claim 65,535 columns; each is a column object, a header and a
        // realized cell per row. Filling ColumnNames one Add at a time also rebuilt
        // the grid per column, so 5,000 columns used to build 12.5 million.
        await Ui.Run(async () =>
        {
            using var invariant = InvariantCulture.Scope();
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);

            const int count = 5_000;
            var columns = Enumerable.Range(1, count).Select(i => new ColumnInfo($"c{i}", "integer", typeof(int))).ToList();
            var row = Enumerable.Range(1, count).Select(i => (object?)i).ToArray();

            vm.ActiveTab.SeedResult(columns, [row]);
            Ui.Settle();

            var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
            await Assert.That(grid.Columns.Count).IsEqualTo(QueryViewModel.MaxGridColumns);
            await Assert.That(vm.ActiveTab.ColumnNames.Count).IsEqualTo(count);
            await Assert.That(vm.ActiveTab.CapStatusText).IsEqualTo($"showing {QueryViewModel.MaxGridColumns:N0} of 5,000 columns");
            // The row still holds every value; only the grid stops building.
            await Assert.That(vm.ActiveTab.Rows[0].Length).IsEqualTo(count);

            vm.ActiveTab.SeedResult([new ColumnInfo("id", "integer", typeof(int))], [[1]], capText: "capped");
            Ui.Settle();
            await Assert.That(grid.Columns.Count).IsEqualTo(1);
            await Assert.That(vm.ActiveTab.CapStatusText).IsEqualTo("capped");

            window.Close();
            Ui.Settle();
        });
    }

    [Test]
    [Timeout(120_000)]
    public async Task Hundred_megabyte_cells_trip_the_byte_cap(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to run the query against.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await Ui.Run(async () =>
        {
            // 100 million characters is 200 MB as a .NET string: the first row fits
            // the 256 MB budget, the second doesn't, and the third is never read.
            var tab = new QueryViewModel(new QueryEngine(dataSource), new ExplainService(dataSource))
            {
                Sql = "SELECT repeat('x', 100000000) AS big FROM generate_series(1, 3)",
            };
            await tab.RunCommand.ExecuteAsync(null);

            await Assert.That(tab.HasError).IsFalse();
            await Assert.That(tab.Rows.Count).IsEqualTo(1);
            await Assert.That(tab.CapText).IsEqualTo(QueryViewModel.CapTextFor(ResultCap.Bytes));
        });
    }

    /// <summary>An in-memory stand-in for the engine's batch stream that records how far it was read.</summary>
    private sealed class ProducedBatches(int batches, int rowsPerBatch, int textLength)
    {
        public int Yielded { get; private set; }

        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<RowBatch> Read()
        {
            try
            {
                for (var b = 0; b < batches; b++)
                {
                    await Task.Yield();
                    var rows = new List<object?[]>(rowsPerBatch);
                    for (var r = 0; r < rowsPerBatch; r++)
                    {
                        rows.Add([(b * rowsPerBatch) + r, new string('x', textLength)]);
                    }

                    Yielded++;
                    yield return new RowBatch(rows);
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }

    /// <summary>
    /// The app runs with InvariantGlobalization, so its <c>N0</c> text is always
    /// "100,000". The test host does not: on a Mac set to a Czech region the same
    /// call writes "100 000" and these assertions failed for reasons that have
    /// nothing to do with the code. Pin the thread to the culture the app has.
    /// </summary>
    private static class InvariantCulture
    {
        public static IDisposable Scope()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            return new Restore(previous);
        }

        private sealed class Restore(System.Globalization.CultureInfo previous) : IDisposable
        {
            public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
