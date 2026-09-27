using System.Text;
using Avalonia.Threading;
using Npgsql;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Export;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Export used to write the grid: one 100-row page when browsing a table, at
/// most 100,000 rows for a query, and no word that anything was missing. It now
/// writes every row. When the grid doesn't hold the whole result, the statement
/// runs again with no limit and streams to the file. It is only run again when
/// that is safe, and when it isn't, the status line says the file is partial.
/// The first half of this file checks that decision without a server; the
/// second half (gated on <c>PGNIMBUS_TEST_CONN</c>) exports against a real one.
/// </summary>
[NotInParallel]
public class ResultExportTests
{
    private static readonly ColumnInfo[] Columns =
    [
        new("id", "integer", typeof(int)),
        new("name", "text", typeof(string)),
    ];

    private static readonly object?[][] ThreeRows = [[1, "a"], [2, "b"], [3, "c"]];

    private static QueryViewModel OfflineTab() =>
        new(new QueryEngine(Fixtures.DataSource), new ExplainService(Fixtures.DataSource));

    private static async Task<(bool Complete, string Text)> Export(QueryViewModel tab, ExportFormat format = ExportFormat.Csv)
    {
        using var stream = new MemoryStream();
        var complete = await tab.ExportAsync(format, stream, "out.csv");
        return (complete, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task A_result_the_grid_holds_whole_is_written_as_it_is()
    {
        await Ui.Run(async () =>
        {
            var tab = OfflineTab();
            tab.SeedResult(Columns, ThreeRows, executedSql: "SELECT id, name FROM t");

            var source = tab.ChooseExportSource();
            await Assert.That(source.Sql).IsNull();
            await Assert.That(source.Shortfall).IsNull();

            var (complete, text) = await Export(tab);
            await Assert.That(complete).IsTrue();
            await Assert.That(Lines(text)).IsEquivalentTo(new[] { "id,name", "1,a", "2,b", "3,c" });
            await Assert.That(tab.Status).IsEqualTo("Exported 3 rows to out.csv");
        });
    }

    [Test]
    public async Task A_cut_off_read_query_is_run_again_for_every_row()
    {
        await Ui.Run(async () =>
        {
            var tab = OfflineTab();
            tab.SeedResult(Columns, ThreeRows, executedSql: "SELECT id, name FROM big", capText: "capped");

            var source = tab.ChooseExportSource();
            await Assert.That(source.Sql).IsEqualTo("SELECT id, name FROM big");
            await Assert.That(source.Vouched).IsFalse();
        });
    }

    [Test]
    public async Task A_cut_off_query_that_writes_is_not_run_again_and_says_so()
    {
        await Ui.Run(async () =>
        {
            // Running this again to export it would insert its rows a second time.
            var tab = OfflineTab();
            tab.SeedResult(Columns, ThreeRows, executedSql: "INSERT INTO t SELECT * FROM staging RETURNING id, name", capText: "capped");

            var source = tab.ChooseExportSource();
            await Assert.That(source.Sql).IsNull();
            await Assert.That(source.Shortfall).IsNotNull();

            var (complete, text) = await Export(tab);
            await Assert.That(complete).IsTrue();
            await Assert.That(Lines(text).Length).IsEqualTo(4);
            await Assert.That(tab.Status).StartsWith("Exported only the 3 rows shown to out.csv: ");
        });
    }

    [Test]
    public async Task A_browsed_table_exports_every_filtered_row_in_page_order()
    {
        ColumnDetail[] columns = [new("id", "integer", true, true), new("name", "text", false, false)];
        var browse = new TableBrowseViewModel("sales", "orders", columns, _ => Task.FromResult(0))
        {
            FilterText = "id > 10",
            Offset = 300,
        };

        var sql = browse.BuildExportSql();
        await Assert.That(sql).IsEqualTo("SELECT * FROM \"sales\".\"orders\"\nWHERE id > 10\nORDER BY \"id\"");
        await Assert.That(browse.BuildSql()).IsEqualTo(sql + "\nLIMIT 100 OFFSET 300");
    }

    // --- Against a real server ----------------------------------------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    private const string Table = "pgnimbus_export_scratch";

    private static void SkipIfNoConnection()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to export from.");
        }
    }

    private static async Task SeedTableAsync(NpgsqlDataSource dataSource, int rows)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            DROP TABLE IF EXISTS public.{Table};
            CREATE TABLE public.{Table} (id int PRIMARY KEY, name text);
            INSERT INTO public.{Table} SELECT g, 'row ' || g FROM generate_series(1, {rows}) g;
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropTableAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand($"DROP TABLE IF EXISTS public.{Table}");
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task Exporting_a_browsed_table_writes_every_row_not_the_page()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await SeedTableAsync(dataSource, 250);
        try
        {
            await Ui.Run(async () =>
            {
                var schema = new SchemaService(dataSource);
                var tab = new QueryViewModel(new QueryEngine(dataSource), new ExplainService(dataSource), schemaService: schema);
                await tab.StartBrowseAsync("public", Table, await schema.GetColumnsAsync("public", Table, CancellationToken.None));
                await Assert.That(tab.Rows.Count).IsEqualTo(TableBrowseViewModel.DefaultPageSize);

                // From the second page, too: the page on screen doesn't matter.
                var browse = tab.Browse!;
                await browse.NextPageCommand.ExecuteAsync(null);

                var (complete, text) = await Export(tab);
                await Assert.That(complete).IsTrue();
                var lines = Lines(text);
                await Assert.That(lines.Length).IsEqualTo(251);
                await Assert.That(lines[1]).IsEqualTo("1,row 1");
                await Assert.That(lines[250]).IsEqualTo("250,row 250");
                await Assert.That(tab.Status).IsEqualTo("Exported 250 rows to out.csv");

                // A filter chip narrows the export exactly as it narrows the grid.
                browse.FilterText = "id % 10 = 0";
                browse.Offset = 0;
                await browse.LoadAsync();
                (complete, text) = await Export(tab, ExportFormat.Json);
                await Assert.That(complete).IsTrue();
                await Assert.That(text.Split("\"id\":").Length - 1).IsEqualTo(25);
            });
        }
        finally
        {
            await DropTableAsync(dataSource);
        }
    }

    [Test]
    public async Task Exporting_a_query_past_the_grid_cap_writes_every_row()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);

        await Ui.Run(async () =>
        {
            const int total = QueryViewModel.MaxDisplayRows + 25;
            var tab = new QueryViewModel(new QueryEngine(dataSource), new ExplainService(dataSource))
            {
                Sql = $"SELECT g AS n FROM generate_series(1, {total}) g",
            };
            await tab.RunCommand.ExecuteAsync(null);
            await Assert.That(tab.Rows.Count).IsEqualTo(QueryViewModel.MaxDisplayRows);
            await Assert.That(tab.CapText).IsNotNull();

            var (complete, text) = await Export(tab);
            await Assert.That(complete).IsTrue();
            var lines = Lines(text);
            await Assert.That(lines.Length).IsEqualTo(total + 1);
            await Assert.That(lines[^1]).IsEqualTo(total.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The grid is left as it was.
            await Assert.That(tab.Rows.Count).IsEqualTo(QueryViewModel.MaxDisplayRows);
        });
    }

    [Test]
    public async Task Cancel_stops_an_export_and_reports_it_incomplete()
    {
        SkipIfNoConnection();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);

        await Ui.Run(async () =>
        {
            var tab = new QueryViewModel(new QueryEngine(dataSource), new ExplainService(dataSource))
            {
                Sql = "SELECT g AS n FROM generate_series(1, 5000000) g",
            };
            await tab.RunCommand.ExecuteAsync(null);

            // Cancel the moment the first bytes reach the file, the way a user
            // presses Cancel while the status line counts up.
            using var stream = new FirstWriteStream(() => Dispatcher.UIThread.Post(() => tab.CancelCommand.Execute(null)));
            var complete = await tab.ExportAsync(ExportFormat.Csv, stream, "out.csv");

            await Assert.That(complete).IsFalse();
            await Assert.That(tab.Status).IsEqualTo("Export cancelled");
            await Assert.That(tab.IsRunning).IsFalse();
            // The whole result is ~43 MB of CSV.
            await Assert.That(stream.Length).IsLessThan(20_000_000L);
        });
    }

    // A MemoryStream that runs an action on its first write.
    private sealed class FirstWriteStream(Action onFirstWrite) : MemoryStream
    {
        private Action? _onFirstWrite = onFirstWrite;

        public override void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Exchange(ref _onFirstWrite, null)?.Invoke();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Interlocked.Exchange(ref _onFirstWrite, null)?.Invoke();
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _onFirstWrite, null)?.Invoke();
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _onFirstWrite, null)?.Invoke();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
