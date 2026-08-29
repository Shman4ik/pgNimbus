using Avalonia;

namespace PgNimbus.Screenshot;

/// <summary>
/// One published image: which rendered scenario it comes from and where in the
/// repo it lands.
/// </summary>
/// <param name="Source">File name in the render output, e.g. <c>main-window.light.png</c>.</param>
/// <param name="Destination">Repo-relative path to write.</param>
/// <param name="MinimumSize">
/// Pad the image out to at least this, centred on a backdrop. Only the Microsoft
/// Store needs it: its listing screenshots must be at least 1366x768, and the
/// monitoring windows render smaller than that.
/// </param>
internal sealed record PublishedImage(string Source, string Destination, PixelSize? MinimumSize = null);

/// <summary>
/// The screenshots that face users — the README, the documentation site and the
/// Microsoft Store listing — generated from the same scenarios the
/// visual-regression baselines come from.
///
/// Before this existed they were captured by hand against a live database,
/// which made them go stale silently (a README shot showed a UI two releases
/// old) and leaked real detail into public assets: the previous main-window
/// screenshot published a live Neon hostname. Generated shots are also
/// reproducible, so refreshing them is a diff a reviewer can read rather than a
/// new hand-framed capture.
///
/// Deliberately not covered: the animated GIFs in the README. Those show motion
/// (a cold start, completion being typed, a side-by-side race) and are still
/// recorded by hand.
/// </summary>
internal static class Marketing
{
    /// <summary>Microsoft Store listing screenshots must be at least this big.</summary>
    private static readonly PixelSize StoreMinimum = new(1366, 768);

    public static readonly PublishedImage[] All =
    [
        // README + docs site.
        new("main-window.light.png", "docs/screenshots/main-light.png"),
        new("main-window.dark.png", "docs/screenshots/main-dark.png"),
        new("main-window-palette.light.png", "docs/screenshots/command-palette.png"),
        new("main-window-plan-tree.light.png", "docs/screenshots/explain-visualization.png"),
        new("activity-window.light.png", "docs/screenshots/server-activity.png"),
        new("notify-window.light.png", "docs/screenshots/notify-monitor.png"),
        new("shortcuts-window.light.png", "docs/screenshots/shortcuts.png"),
        new("connection-dialog.light.png", "docs/screenshots/connection-dialog.png"),

        // Microsoft Store listing. Numbered because Partner Center orders
        // screenshots by upload and the numbering is the only way to keep the
        // intended sequence across a re-upload.
        new("main-window.light.png", "design/store/screenshots/01-query-results.png", StoreMinimum),
        new("main-window.dark.png", "design/store/screenshots/02-dark-theme.png", StoreMinimum),
        new("main-window-plan-tree.light.png", "design/store/screenshots/03-query-plan.png", StoreMinimum),
        new("main-window-palette.light.png", "design/store/screenshots/04-command-palette.png", StoreMinimum),
        new("activity-window.light.png", "design/store/screenshots/05-server-activity.png", StoreMinimum),
        new("database-overview-window.light.png", "design/store/screenshots/06-database-overview.png", StoreMinimum),
    ];

    /// <summary>
    /// Copies every published image out of a completed render into the repo.
    /// Fails rather than skipping when a source is missing: a partial publish
    /// would leave some assets refreshed and others stale, which is worse than
    /// not publishing at all.
    /// </summary>
    public static void Publish(string renderedDir, string repoRoot)
    {
        foreach (var image in All)
        {
            var source = Path.Combine(renderedDir, image.Source);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    $"{image.Destination} needs {image.Source}, which the render did not produce. " +
                    "Publishing runs over the full scenario set — drop the scenario filter.",
                    source);
            }

            var destination = Path.Combine(repoRoot, image.Destination);
            var pixels = Png.Read(source);

            if (image.MinimumSize is { } minimum &&
                (pixels.Width < minimum.Width || pixels.Height < minimum.Height))
            {
                pixels = Pad(pixels, minimum);
            }

            Png.Write(destination, pixels);
            Console.WriteLine($"Published {image.Destination}  ({pixels.Width}x{pixels.Height})");
        }
    }

    /// <summary>
    /// Centres an image on a larger canvas filled with its own top-left pixel.
    /// That pixel is the window chrome's background, so the padding matches the
    /// shot's own theme without the theme having to be passed in.
    /// </summary>
    private static Pixels Pad(Pixels source, PixelSize minimum)
    {
        var width = Math.Max(source.Width, minimum.Width);
        var height = Math.Max(source.Height, minimum.Height);
        var padded = new Pixels(width, height, new byte[width * height * 4]);

        var backdrop = source.Data.AsSpan(source.Offset(0, 0), 4).ToArray();
        for (var i = 0; i < padded.Data.Length; i += 4)
        {
            padded.Data[i] = backdrop[0];
            padded.Data[i + 1] = backdrop[1];
            padded.Data[i + 2] = backdrop[2];
            padded.Data[i + 3] = 0xFF;
        }

        var left = (width - source.Width) / 2;
        var top = (height - source.Height) / 2;
        for (var y = 0; y < source.Height; y++)
        {
            Array.Copy(
                source.Data,
                source.Offset(0, y),
                padded.Data,
                padded.Offset(left, top + y),
                source.Stride);
        }

        return padded;
    }
}
