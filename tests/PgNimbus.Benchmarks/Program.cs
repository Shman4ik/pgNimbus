using System.Diagnostics;
using System.Text;
using Npgsql;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Export;
using PgNimbus.Core.Query;
using PgNimbus.Core.Text;

// Query-engine benchmarks for pgNimbus, run by scripts/benchmarks/run-benchmarks.sh
// and the Benchmarks CI workflow. Measures the engine the way the app uses it —
// through QueryEngine's streaming IAsyncEnumerable<RowBatch> path — against a real
// PostgreSQL server, and prints one machine-readable line per metric:
//
//   PGNIMBUS_BENCH connect_ms=12.3
//
// Configuration (env vars):
//   PGNIMBUS_BENCH_CONN  connection string, any format ConnectionStringParser
//                        understands (default: localhost/postgres/postgres)
//   PGNIMBUS_BENCH_ROWS  row count for the streaming benchmarks (default 100000)
//   PGNIMBUS_BENCH_ITERS iterations per metric; the median is reported (default 5)
//   PGNIMBUS_BENCH_SKIP_DB=1  only the in-process metrics below, no server needed
//
// The in-process metrics come first and need no server. They time the Core work
// that sat on the UI thread and grew with the data until the 2026-09 UI-thread
// audit (docs/dev/design/ui-thread-audit.md); tools/UiBench times the views.

var rawConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_CONN")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
var rows = int.TryParse(Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_ROWS"), out var r) ? r : 100_000;
var iterations = int.TryParse(Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_ITERS"), out var i) ? i : 5;

// --- in-process: Core work that grows with the data ------------------------
{
    // Safe mode, select all + Delete: 100,000 rows staged, then asked about one
    // by one as the grid tints each row it realizes. Quadratic until the staged
    // set was indexed.
    var stageMs = Median(iterations, () =>
    {
        var stopwatch = Stopwatch.StartNew();
        var pending = new PendingChangeSet("public", "t", ["id"]);
        for (var id = 0; id < 100_000; id++)
        {
            pending.StageDelete([id]);
        }

        for (var id = 0; id < 100_000; id++)
        {
            _ = pending.IsRowDeleted([id]);
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    });

    // Ctrl+A, Ctrl+C over a full grid: 100,000 rows of five mixed columns as TSV.
    var copyColumns = new[] { "id", "name", "placed_at", "amount", "note" };
    var copyRows = Enumerable.Range(0, 100_000)
        .Select(n => new object?[] { n, $"customer {n}", new DateTime(2026, 1, 1).AddMinutes(n), n * 1.25m, n % 7 == 0 ? null : "ok" })
        .ToList();
    var copyMs = Median(iterations, () =>
    {
        var stopwatch = Stopwatch.StartNew();
        using var writer = new StringWriter();
        ResultExporter.WriteTsv(writer, copyColumns, copyRows);
        return stopwatch.Elapsed.TotalMilliseconds;
    });

    // Writing the history after a run: a full history of 200 entries, five of
    // them 1 MB scripts, redacted on the way out.
    var historyPath = Path.Combine(Path.GetTempPath(), $"pgnimbus-bench-history-{Environment.ProcessId}.json");
    var script = string.Concat(Enumerable.Repeat("INSERT INTO t (a, b) VALUES (1, 'x');\n", 28_000));
    var store = new QueryHistoryStore(historyPath);
    store.Save([.. Enumerable.Range(0, 200).Select(n => new QueryHistoryEntry(
        n % 40 == 0 ? script : $"SELECT {n} FROM t WHERE id = {n};", DateTimeOffset.UtcNow, 1, "1 row"))]);
    var historyMs = Median(iterations, () =>
    {
        var stopwatch = Stopwatch.StartNew();
        store.Append(new QueryHistoryEntry("SELECT 1;", DateTimeOffset.UtcNow, 1, "1 row"));
        return stopwatch.Elapsed.TotalMilliseconds;
    });
    File.Delete(historyPath);

    // A keystroke at the end of a 5 MB script: finding the statement the caret is
    // in, which the editor's readers did by lexing the whole document per key.
    var dump = new StringBuilder();
    while (dump.Length < 5_000_000)
    {
        dump.Append("INSERT INTO events (id, payload) VALUES (1, '{\"a\": \"x;y\"}');\n");
    }

    dump.Append("SELECT count(*) FROM events WHERE ");
    var document = dump.ToString();
    var boundaries = new SqlStatementBoundaries();
    boundaries.Span(document, document.Length);
    var keystrokeMs = Median(iterations, () =>
    {
        // Each round is what one typed character at the end costs: the edit
        // invalidates what follows it, and the reader asks for the statement
        // again. (The text itself stays put, so the timing isn't a 5 MB copy.)
        var stopwatch = Stopwatch.StartNew();
        for (var key = 0; key < 100; key++)
        {
            boundaries.Invalidate(document.Length - 1);
            boundaries.Span(document, document.Length);
        }

        return stopwatch.Elapsed.TotalMilliseconds / 100;
    });

    Console.WriteLine($"PGNIMBUS_BENCH stage_deletes_ms={stageMs:F1}");
    Console.WriteLine($"PGNIMBUS_BENCH copy_tsv_ms={copyMs:F1}");
    Console.WriteLine($"PGNIMBUS_BENCH history_append_ms={historyMs:F1}");
    Console.WriteLine($"PGNIMBUS_BENCH editor_statement_ms={keystrokeMs:F3}");
}

if (Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_SKIP_DB") == "1")
{
    return 0;
}

var connectionString = ConnectionStringParser.NormalizeToNpgsql(rawConnectionString);

// A realistic mixed-type row (int, text, timestamp, numeric) so the streaming
// numbers reflect real column materialization, not just integer shuffling.
var streamSql = $"""
    SELECT g AS id,
           'row #' || g AS name,
           now() + (g || ' seconds')::interval AS ts,
           (g % 100000)::numeric / 7 AS amount
      FROM generate_series(1, {rows}) g
    """;

// --- connect: first physical connection on a cold pool ---------------------
double connectMs;
{
    var stopwatch = Stopwatch.StartNew();
    await using var coldDataSource = NpgsqlDataSource.Create(connectionString);
    await using (await coldDataSource.OpenConnectionAsync())
    {
        connectMs = stopwatch.Elapsed.TotalMilliseconds;
    }
}

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var engine = new QueryEngine(dataSource);

// Warm the pool and the JIT before anything is timed.
await DrainAsync(engine, "SELECT 1");

// --- roundtrip: SELECT 1 on a warm pooled connection ------------------------
var roundtripMs = await MedianAsync(iterations, async () =>
{
    var stopwatch = Stopwatch.StartNew();
    await DrainAsync(engine, "SELECT 1");
    return stopwatch.Elapsed.TotalMilliseconds;
});

// --- first_batch: call → first RowBatch of a large streaming result --------
// The number behind "the first screenful renders before the full result set
// arrives" — how long until the UI has rows it can paint.
var firstBatchMs = await MedianAsync(iterations, async () =>
{
    var stopwatch = Stopwatch.StartNew();
    var result = await engine.ExecuteAsync(streamSql, CancellationToken.None);
    if (result is not ResultSet resultSet)
    {
        throw new InvalidOperationException($"Expected a ResultSet, got {result}");
    }

    double elapsed = -1;
    await foreach (var batch in resultSet.Batches)
    {
        if (elapsed < 0)
        {
            elapsed = stopwatch.Elapsed.TotalMilliseconds;
        }
        // Keep draining: disposal drains the remaining rows anyway (to leave
        // the connection usable), so stopping early would still pay full cost.
    }

    return elapsed;
});

// --- stream: full drain of the large result through RowBatch-es ------------
long streamedRows = 0;
var streamMs = await MedianAsync(iterations, async () =>
{
    var stopwatch = Stopwatch.StartNew();
    streamedRows = await DrainAsync(engine, streamSql);
    return stopwatch.Elapsed.TotalMilliseconds;
});

if (streamedRows != rows)
{
    throw new InvalidOperationException($"Streamed {streamedRows} rows, expected {rows}");
}

var rowsPerSec = rows / (streamMs / 1000.0);

Console.WriteLine($"PGNIMBUS_BENCH connect_ms={connectMs:F1}");
Console.WriteLine($"PGNIMBUS_BENCH roundtrip_ms={roundtripMs:F2}");
Console.WriteLine($"PGNIMBUS_BENCH first_batch_ms={firstBatchMs:F1}");
Console.WriteLine($"PGNIMBUS_BENCH stream_ms={streamMs:F1}");
Console.WriteLine($"PGNIMBUS_BENCH stream_rows={rows}");
Console.WriteLine($"PGNIMBUS_BENCH rows_per_sec={rowsPerSec:F0}");
return 0;

static async Task<long> DrainAsync(QueryEngine engine, string sql)
{
    var result = await engine.ExecuteAsync(sql, CancellationToken.None);
    if (result is QueryError error)
    {
        throw new InvalidOperationException($"Query failed: {error.Message}");
    }

    long count = 0;
    if (result is ResultSet resultSet)
    {
        await foreach (var batch in resultSet.Batches)
        {
            count += batch.Rows.Count;
        }
    }

    return count;
}

static double Median(int iterations, Func<double> measure)
{
    var samples = new List<double>(iterations);
    for (var i = 0; i < iterations; i++)
    {
        samples.Add(measure());
    }

    samples.Sort();
    var mid = samples.Count / 2;
    return samples.Count % 2 == 1 ? samples[mid] : (samples[mid - 1] + samples[mid]) / 2.0;
}

static async Task<double> MedianAsync(int iterations, Func<Task<double>> measure)
{
    var samples = new List<double>(iterations);
    for (var i = 0; i < iterations; i++)
    {
        samples.Add(await measure());
    }

    samples.Sort();
    var mid = samples.Count / 2;
    return samples.Count % 2 == 1 ? samples[mid] : (samples[mid - 1] + samples[mid]) / 2.0;
}
