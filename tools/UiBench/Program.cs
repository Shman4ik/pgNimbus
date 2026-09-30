using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

// UI-thread timings for the views the 2026-09 UI-thread audit fixed
// (docs/dev/design/ui-thread-audit.md). Each metric is the time the UI thread
// spends on one user-visible step over big data (expanding a schema of 5,000
// tables, one keystroke in the palette over 50,000 relations, a key typed at the
// end of a 5 MB script), measured on Avalonia's headless platform with the real
// views and the screenshot harness's fixtures: no display, no Postgres. The
// *_rows / *_chips metrics count the rows a view realized, the number that
// virtualization keeps at a screenful.
//
// Prints one line per metric, the median of PGNIMBUS_BENCH_UI_ITERS runs (default 3):
//
//   PGNIMBUS_BENCH ui_schema_expand_ms=123.4
//
// Every metric is smaller-is-better, as benchmarks.json requires. Run it in Release:
//
//   dotnet run --project tools/UiBench -c Release
//
// Headless numbers are software-rendered and machine-relative; the point is the
// trend from one release to the next, not the absolute value.

// Before anything builds a store: nothing here may reach the developer's real
// settings, saved queries or history.
IsolatedAppData.Enable();

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var iterations = int.TryParse(Environment.GetEnvironmentVariable("PGNIMBUS_BENCH_UI_ITERS"), out var n) && n > 0 ? n : 3;
var metrics = new List<(string Name, double Value, string Format)>();

void Report(string name, IEnumerable<double> samples, string format = "F1")
{
    var sorted = samples.OrderBy(v => v).ToList();
    var mid = sorted.Count / 2;
    var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    metrics.Add((name, median, format));
    Console.WriteLine($"PGNIMBUS_BENCH {name}={median.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}");
}

// --- schema tree: expand a schema of 5,000 tables, then filter it to one ----
{
    var expand = new List<double>();
    var rows = new List<double>();
    var filter = new List<double>();
    for (var run = 0; run < iterations; run++)
    {
        var (window, vm) = Scenarios.Shell();
        Show(window);
        var service = new SchemaService(Fixtures.DataSource);
        var schema = new SchemaNode(service, "big", () => false, () => false);
        schema.SeedChildren(Enumerable.Range(0, 5_000)
            .Select(i => (SchemaTreeNode)new TableNode(service, "big", $"table_{i:D5}", RelationKind.Table)));
        vm.SchemaTree.Schemas.Insert(0, schema);
        Settle();

        expand.Add(Time(() => schema.IsExpanded = true));
        rows.Add(window.GetVisualDescendants().OfType<TreeViewItem>().Count(i => i.DataContext is TableNode { Schema: "big" }));
        filter.Add(Time(() => vm.SchemaTree.FilterText = "table_04999"));
        window.Close();
        Settle();
    }

    Report("ui_schema_expand_ms", expand);
    Report("ui_schema_expand_rows", rows, "F0");
    Report("ui_schema_filter_ms", filter);
}

// --- a script of 5,000 statements: its section strip ------------------------
{
    var sections = new List<double>();
    var chips = new List<double>();
    for (var run = 0; run < iterations; run++)
    {
        var vm = Fixtures.MainWindowViewModel();
        var tab = vm.ActiveTab;
        tab.ResultSections.AddRange([Section(1), Section(2)]);
        tab.SelectedSection = tab.ResultSections[0];
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        Show(window);

        sections.Add(Time(() => tab.ResultSections.AddRange([.. Enumerable.Range(3, 4_998).Select(Section)])));
        chips.Add(window.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.DataContext is ScriptResultViewModel));
        window.Close();
        Settle();
    }

    Report("ui_script_sections_ms", sections);
    Report("ui_script_section_chips", chips, "F0");
}

// --- the command palette over 50,000 relations: one keystroke ---------------
{
    var keys = new List<double>();
    for (var run = 0; run < iterations; run++)
    {
        var (window, vm) = Scenarios.Shell();
        Show(window);
        var random = new Random(run);
        vm.CommandPalette.Open([.. Enumerable.Range(0, 50_000).Select(i => new PaletteItem(
            $"schema_{random.Next(60)}.orders_{i}", "Table", "▦", () => Task.CompletedTask))]);
        Settle();
        foreach (var query in new[] { "o", "or", "ord", "orde", "order", "orders_1" })
        {
            keys.Add(Time(() => vm.CommandPalette.SearchText = query));
        }

        vm.CommandPalette.CloseCommand.Execute(null);
        window.Close();
        Settle();
    }

    Report("ui_palette_key_ms", keys);
}

// --- a result as wide as the grid shows: rows landing, then its edit context --
{
    var seed = new List<double>();
    var context = new List<double>();
    for (var run = 0; run < iterations; run++)
    {
        var (window, vm) = Scenarios.Shell();
        Show(window);
        var width = QueryViewModel.MaxGridColumns;
        var columns = Enumerable.Range(0, width).Select(c => new ColumnInfo($"c{c}", "text", typeof(string))).ToList();
        var rows = Enumerable.Range(0, 2_000).Select(r => Enumerable.Range(0, width).Select(c => (object?)$"v{r}.{c}").ToArray()).ToList();
        seed.Add(Time(() => vm.ActiveTab.SeedResult(columns, rows)));
        context.Add(Time(() => vm.ActiveTab.EditContext = new EditableTableContext("public", "wide", ["c0"], [])));
        window.Close();
        Settle();
    }

    Report("ui_wide_result_ms", seed);
    Report("ui_edit_context_ms", context);
}

// --- a megabyte of read-only text: a big plan's text view, a big jsonb cell --
{
    var text = new StringBuilder();
    for (var i = 0; text.Length < 1_000_000; i++)
    {
        text.Append($"  ->  Seq Scan on events_p{i:D4}  (cost=0.00..1834.00 rows=100000 width=48) (actual time=0.011..9.812 rows=100000 loops=1)\n");
    }

    var plan = text.ToString();
    var shows = new List<double>();
    for (var run = 0; run < iterations; run++)
    {
        var view = new ReadOnlyTextView();
        var window = new Window { Width = 1200, Height = 800, Content = view };
        Show(window);
        shows.Add(Time(() => view.Text = plan));
        window.Close();
        Settle();
    }

    Report("ui_large_text_ms", shows);
}

// --- typing at the end of a 5 MB script -------------------------------------
{
    var script = new StringBuilder();
    while (script.Length < 5_000_000)
    {
        script.Append("INSERT INTO events (id, payload) VALUES (1, '{\"note\": \"a;b\"}');\n");
    }

    script.Append("SELECT coalesce");
    var sql = script.ToString();
    var keys = new List<double>();
    for (var run = 0; run < iterations; run++)
    {
        var (window, vm) = Scenarios.Shell();
        Show(window);
        vm.ActiveTab.Sql = sql;
        Settle();
        var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
        editor.CaretOffset = editor.Document.TextLength;
        editor.TextArea.Focus();
        Settle();

        // '(' opens the argument hint (and a closing ')'), ',' asks the clause of
        // the caret, and every caret move then re-reads the call: the keys that
        // lexed the whole document.
        foreach (var key in new[] { "(", "1", ",", " ", "2", ",", " ", "3" })
        {
            keys.Add(Time(() => window.KeyTextInput(key)));
        }

        window.Close();
        Settle();
    }

    Report("ui_editor_key_ms", keys);
}

// --- running a query with a full history in memory ---------------------------
{
    var records = new List<double>();
    var big = string.Concat(Enumerable.Repeat("INSERT INTO t (a, b) VALUES (1, 'x');\n", 28_000));
    for (var run = 0; run < iterations; run++)
    {
        var vm = Fixtures.MainWindowViewModel();
        var history = vm.SavedQueries;
        for (var i = 0; i < QueryHistoryStore.MaxEntries; i++)
        {
            history.History.Add(new QueryHistoryEntry(i % 40 == 0 ? big : $"SELECT {i};", DateTimeOffset.UtcNow, 1, "1 row"));
        }

        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        Show(window);
        for (var i = 0; i < 10; i++)
        {
            records.Add(Time(() => history.RecordExecution(new QueryHistoryEntry($"SELECT {i};", DateTimeOffset.UtcNow, 1, "1 row"))));
        }

        history.PendingHistoryWrite.GetAwaiter().GetResult();
        window.Close();
        Settle();
    }

    Report("ui_history_record_ms", records, "F2");
}

return 0;

static ScriptResultViewModel Section(int index) => ScriptResultViewModel.From(
    index, $"INSERT INTO t VALUES ({index})",
    new CommandResult { Elapsed = TimeSpan.FromMilliseconds(1), RowsAffected = 1, CommandTag = "INSERT 0 1" });

// The action and everything it queued on the UI thread, laid out and rendered.
static double Time(Action action)
{
    var stopwatch = Stopwatch.StartNew();
    action();
    Settle();
    return stopwatch.Elapsed.TotalMilliseconds;
}

static void Show(Window window)
{
    window.Show();
    Settle();
}

static void Settle()
{
    for (var i = 0; i < 2; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
