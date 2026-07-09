using System.Diagnostics;
using Npgsql;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Query;

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

var rawConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_CONN")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
var rows = int.TryParse(Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_ROWS"), out var r) ? r : 100_000;
var iterations = int.TryParse(Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_ITERS"), out var i) ? i : 5;

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
