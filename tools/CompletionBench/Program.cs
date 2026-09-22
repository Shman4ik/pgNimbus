using System.Diagnostics;
using System.Text;
using PgNimbus.App.Completion;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

// Measures what one keystroke in the SQL editor costs on the UI thread, over
// documents of 1/10/100 thousand characters and catalogs up to a million
// columns. Synthetic on purpose: no user SQL, no live database. Prints one
// Markdown table per catalog; medians and p95 over the timed runs.

var documentSizes = new[] { 1_000, 10_000, 100_000 };
var catalogs = new (string Label, int Tables, int ColumnsPerTable)[]
{
    ("3 tables", 3, 3),
    ("1k tables / 10k columns", 1_000, 10),
    ("10k tables / 100k columns", 10_000, 10),
    ("100k tables / 1M columns", 100_000, 10),
};

foreach (var (label, tableCount, columnCount) in catalogs)
{
    var provider = new SqlCompletionProvider(null);
    var catalog = BuildCatalog(tableCount, columnCount);
    var load = Stopwatch.StartNew();
    provider.Load(catalog);
    load.Stop();
    Console.WriteLine($"## {label} — snapshot build {load.Elapsed.TotalMilliseconds:F0} ms, {GC.GetTotalMemory(true) / (1024 * 1024)} MB managed heap");
    Console.WriteLine();
    Console.WriteLine("| Operation | Document | Median, ms | p95, ms | Allocated per call |");
    Console.WriteLine("| --- | --- | --- | --- | --- |");

    foreach (var size in documentSizes)
    {
        var (text, caretEnd, caretMiddle) = BuildDocument(size);
        Report("Open list: GetCompletionData, caret at end", size, () => provider.GetCompletionData(text, caretEnd));
        Report("Open list: GetCompletionData, caret mid-document", size, () => provider.GetCompletionData(text, caretMiddle));
        var data = provider.GetCompletionData(text, caretEnd);
        Report($"Keep typing: rank {data.Count} candidates", size,
            () => CompletionRanker.Rank(data, "na", static d => d.Text, static d => d.Priority, static _ => -1));
        Report("Caret context (auto-close pairs, triggers)", size, () => SqlCompletionContext.GetCaretContext(text, caretEnd));
        var bare = text + "; SELECT col";
        Report("Open list, no FROM yet: GetCompletionData (whole catalog)", size, () => provider.GetCompletionData(bare, bare.Length));
        var everything = provider.GetCompletionData(bare, bare.Length);
        Report($"Keep typing, no FROM yet: rank {everything.Count} candidates", size,
            () => CompletionRanker.Rank(everything, "col_", static d => d.Text, static d => d.Priority, static _ => -1));
        CompletionRanker.Rank(everything, "col_", static d => d.Text, static d => d.Priority, static _ => -1, null, out var pool);
        Report($"Next keystroke, narrowed to {pool.Count} previous matches", size,
            () => CompletionRanker.Rank(everything, "col_1", static d => d.Text, static d => d.Priority, static _ => -1, pool, out _));
        Report("Argument hint: GetSignatureHints", size, () => provider.GetSignatureHints(text, caretEnd));
    }

    Console.WriteLine();
}

static void Report(string operation, int size, Func<object?> action)
{
    for (var i = 0; i < 5; i++)
    {
        action();
    }

    const int runs = 30;
    var times = new double[runs];
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < runs; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        times[i] = sw.Elapsed.TotalMilliseconds;
    }

    var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / runs;
    Array.Sort(times);
    Console.WriteLine($"| {operation} | {size / 1000}k | {times[runs / 2]:F3} | {times[(int)(runs * 0.95)]:F3} | {allocated / 1024:N0} KB |");
}

// A script of finished statements with the edited one last: the shape the
// audit measured, where cost grew with the whole buffer.
static (string Text, int CaretEnd, int CaretMiddle) BuildDocument(int size)
{
    var sb = new StringBuilder();
    var n = 0;
    const string edited = "SELECT u.name, round(o.total, ";
    while (sb.Length < size - 200)
    {
        sb.Append($"SELECT t.col_{n % 10}, count(*) FROM public.table_{n % 50} t WHERE t.col_1 = 'x;{n}' GROUP BY 1;\n");
        n++;
    }

    var middle = sb.Length / 2;
    middle = sb.ToString().IndexOf("WHERE ", middle, StringComparison.Ordinal) + "WHERE ".Length;
    sb.Append("SELECT * FROM public.users u JOIN public.orders o ON o.user_id = u.id WHERE ");
    sb.Append(edited);
    return (sb.ToString(), sb.Length, middle);
}

static CompletionCatalog BuildCatalog(int tableCount, int columnCount)
{
    var tables = new List<CompletionTable>(tableCount + 2)
    {
        new("public", "users", [new("users", "id", "int4"), new("users", "name", "text")]),
        new("public", "orders", [new("orders", "id", "int4"), new("orders", "user_id", "int4"), new("orders", "total", "numeric")]),
    };

    for (var t = 0; t < tableCount; t++)
    {
        var name = $"table_{t}";
        tables.Add(new CompletionTable(t % 2 == 0 ? "public" : "sales", name,
            [.. Enumerable.Range(0, columnCount).Select(c => new TableColumn(name, $"col_{c}", "text"))]));
    }

    return new CompletionCatalog(["public", "sales"], tables, [], [], ["public"])
    {
        BuiltinFunctions = [new CompletionFunction("pg_catalog", new FunctionInfo("round", "numeric, integer", "numeric", 'f'))],
    };
}
