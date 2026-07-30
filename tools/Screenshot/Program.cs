using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using PgNimbus.App;
using PgNimbus.Screenshot;

// Usage: dotnet run --project tools/Screenshot -- <outputDir> [scenario-substring]
//
// Renders every scenario in light and dark and writes one PNG per (scenario,
// theme). No display, no xdotool, no Postgres — see CLAUDE.md, "Headless
// screenshot harness".

var outDir = args.Length > 0 ? args[0] : "screenshots";
var filter = args.Length > 1 ? args[1] : null;
Directory.CreateDirectory(outDir);

BuildAvaloniaApp().SetupWithoutStarting();

var scenarios = new (string Name, Func<Window> Build)[]
{
    ("main-window", Scenarios.Results),
    ("main-window-empty", Scenarios.EmptyResults),
    ("main-window-error", Scenarios.QueryError),
    ("main-window-script", Scenarios.ScriptResult),
    ("main-window-plan", Scenarios.QueryPlan),
    ("main-window-plan-tree", Scenarios.QueryPlanTree),
    ("main-window-palette", Scenarios.CommandPalette),
    ("main-window-sidebar-filter", Scenarios.SidebarFilter),
    ("main-window-cell-inspector", Scenarios.CellInspector),
    ("activity-window", Scenarios.Activity),
    ("activity-window-blocking", Scenarios.ActivityBlocking),
    ("database-overview-window", Scenarios.DatabaseOverview),
    ("shortcuts-window", Scenarios.Shortcuts),
    ("preferences-window", Scenarios.Preferences),
    ("crash-window", Scenarios.Crash),
};

var failures = 0;
foreach (var (name, build) in scenarios)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        failures += Capture(name, theme, build) ? 0 : 1;
    }
}

Console.WriteLine($"Wrote screenshots to {Path.GetFullPath(outDir)}");
return failures == 0 ? 0 : 1;

bool Capture(string name, ThemeVariant theme, Func<Window> build)
{
    // Set before the window is built: ActualThemeVariant resolves as the visual
    // tree attaches, and several panels resolve theme-dependent resources then
    // (see CLAUDE.md, UI design rule 7 — "a panel's constructor can't see
    // app-level resources").
    Application.Current!.RequestedThemeVariant = theme;

    var window = build();
    window.Show();

    // Pump the dispatcher, force one render tick, then pump again: layout jobs
    // posted during the first pass (measure/arrange, ItemsSource swaps) have to
    // run before the frame is worth capturing.
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();

    using var frame = window.CaptureRenderedFrame();
    var path = Path.Combine(outDir, $"{name}.{(theme == ThemeVariant.Dark ? "dark" : "light")}.png");
    frame?.Save(path, new PngBitmapEncoderOptions());
    window.Close();

    Console.WriteLine(frame is null ? $"FAILED (no frame): {path}" : $"Wrote {path}");
    return frame is not null;
}

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont();
