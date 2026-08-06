using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using PgNimbus.App;
using PgNimbus.Screenshot;

// Usage: dotnet run --project tools/Screenshot -- <outputDir> [scenario-substring]
//                                                [--baseline <dir>] [--fail-on-new]
//
// Renders every scenario in light and dark and writes one PNG per (scenario,
// theme). No display, no xdotool, no Postgres — see CLAUDE.md, "Headless
// screenshot harness".
//
// With --baseline it also becomes the visual-regression gate: each rendered
// frame is compared against the committed baseline of the same name, and a
// difference beyond tolerance fails the run and leaves a diff image behind.

var positional = new List<string>();
string? baselineDir = null;
string? publishRoot = null;
var failOnNew = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--baseline":
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("--baseline needs a directory");
                return 2;
            }

            baselineDir = args[i];
            break;

        // A scenario with no baseline is normally just a warning: the harness is
        // run from a developer machine long before the baselines (which have to
        // be rendered on the CI OS) catch up, and blocking that would only teach
        // people to skip the check. The refresh workflow passes this to assert
        // it actually produced every baseline it was supposed to.
        case "--fail-on-new":
            failOnNew = true;
            break;

        // Copies the user-facing subset (README, docs site, Store listing) out
        // of this render and into the repo — see Marketing.
        case "--publish":
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("--publish needs the repository root");
                return 2;
            }

            publishRoot = args[i];
            break;

        default:
            positional.Add(args[i]);
            break;
    }
}

var outDir = positional.Count > 0 ? positional[0] : "screenshots";
var filter = positional.Count > 1 ? positional[1] : null;
Directory.CreateDirectory(outDir);

BuildAvaloniaApp().SetupWithoutStarting();

var failures = 0;
var mismatches = new List<string>();
var missingBaselines = new List<string>();

foreach (var (name, build) in Scenarios.All)
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

Console.WriteLine();
Console.WriteLine($"Wrote screenshots to {Path.GetFullPath(outDir)}");

if (publishRoot is not null)
{
    if (failures > 0)
    {
        Console.Error.WriteLine("Not publishing: the render did not come out clean.");
        return 1;
    }

    Console.WriteLine();
    Marketing.Publish(outDir, publishRoot);
}

if (baselineDir is not null)
{
    Console.WriteLine(
        missingBaselines.Count == 0 && mismatches.Count == 0
            ? "Visual regression: every scenario matches its baseline."
            : $"Visual regression: {mismatches.Count} mismatched, {missingBaselines.Count} without a baseline.");

    foreach (var entry in mismatches)
    {
        Console.WriteLine($"  CHANGED  {entry}");
    }

    foreach (var entry in missingBaselines)
    {
        Console.WriteLine($"  NEW      {entry}");
    }

    if (mismatches.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("If the change is intended, refresh the baselines — see docs/design/release-checks.md.");
    }
}

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

    if (frame is null)
    {
        Console.WriteLine($"FAILED (no frame): {path}");
        return false;
    }

    if (baselineDir is null)
    {
        Console.WriteLine($"Wrote {path}");
        return true;
    }

    var result = ImageDiff.Compare(path, baselineDir);
    var label = Path.GetFileName(path);
    Console.WriteLine($"{result.Outcome,-11} {label}  ({result.Message})");

    switch (result.Outcome)
    {
        case ImageDiff.Outcome.Mismatch:
            mismatches.Add($"{label} — {result.Message}");
            return false;

        case ImageDiff.Outcome.NoBaseline:
            missingBaselines.Add(label);
            return !failOnNew;

        default:
            return true;
    }
}

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont();
