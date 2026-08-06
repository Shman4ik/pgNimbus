namespace PgNimbus.Screenshot;

/// <summary>
/// Pixel comparison of a freshly rendered scenario against its committed
/// baseline, plus the diff image a reviewer actually looks at.
///
/// This is the half of the harness that turns the screenshots from "an artifact
/// somebody might open" into a gate: without it a squashed row, a lost binding
/// or a theme brush that stopped resolving renders happily and ships.
/// </summary>
internal static class ImageDiff
{
    /// <summary>
    /// Per-channel delta below which two pixels count as equal. Skia's software
    /// rasterizer is deterministic for a given build, but antialiasing along
    /// glyph and border edges can still shift by a step or two across Skia
    /// versions; a difference that small is invisible and must not fail a run.
    /// </summary>
    private const int ChannelTolerance = 8;

    /// <summary>
    /// Fraction of the image allowed to differ before the comparison fails.
    /// 0.1% of a 1440x900 window is ~1300 pixels. Calibrated against measured
    /// numbers rather than guessed: two renders of the same commit on the same
    /// OS come out bit-identical (0 differing pixels), while the same frames
    /// rendered on Windows versus Linux differ by 0.6-6% purely from glyph
    /// rasterization. So this sits far above the noise floor and far below any
    /// real change — moving a single row of text lights up several thousand
    /// pixels. It is also why baselines are OS-specific and rendered on Linux.
    /// </summary>
    private const double MaxDifferingRatio = 0.001;

    public enum Outcome
    {
        /// <summary>Within tolerance of the baseline.</summary>
        Match,

        /// <summary>Differs from the baseline beyond tolerance.</summary>
        Mismatch,

        /// <summary>No baseline on disk — a newly added scenario.</summary>
        NoBaseline,
    }

    public readonly record struct Result(Outcome Outcome, string Message);

    /// <summary>
    /// Compares <paramref name="renderedPath"/> against the baseline of the same
    /// file name in <paramref name="baselineDir"/>. On a mismatch, writes a diff
    /// image next to the rendered one (suffix <c>.diff.png</c>).
    /// </summary>
    public static Result Compare(string renderedPath, string baselineDir)
    {
        var baselinePath = Path.Combine(baselineDir, Path.GetFileName(renderedPath));
        if (!File.Exists(baselinePath))
        {
            return new Result(Outcome.NoBaseline, $"no baseline at {baselinePath}");
        }

        var rendered = Png.Read(renderedPath);
        var baseline = Png.Read(baselinePath);

        if (rendered.Width != baseline.Width || rendered.Height != baseline.Height)
        {
            return new Result(
                Outcome.Mismatch,
                $"size changed: baseline {baseline.Width}x{baseline.Height}, rendered {rendered.Width}x{rendered.Height}");
        }

        var differing = CountDifferingPixels(rendered.Data, baseline.Data);
        if (differing == 0)
        {
            return new Result(Outcome.Match, "identical");
        }

        var total = rendered.Width * rendered.Height;
        var ratio = (double)differing / total;
        if (ratio <= MaxDifferingRatio)
        {
            return new Result(Outcome.Match, $"{differing} px differ ({ratio:P3}, within tolerance)");
        }

        var diffPath = Path.ChangeExtension(renderedPath, null) + ".diff.png";
        Png.Write(diffPath, BuildDiffImage(rendered, baseline));

        return new Result(
            Outcome.Mismatch,
            $"{differing} px differ ({ratio:P2} > {MaxDifferingRatio:P2}) — diff at {diffPath}");
    }

    private static int CountDifferingPixels(byte[] rendered, byte[] baseline)
    {
        var differing = 0;
        for (var i = 0; i < rendered.Length; i += 4)
        {
            if (IsDifferent(rendered, baseline, i))
            {
                differing++;
            }
        }

        return differing;
    }

    private static bool IsDifferent(byte[] a, byte[] b, int offset)
    {
        // Alpha included: a control that lost its background is a real change
        // even when the colour underneath happens to match.
        for (var channel = 0; channel < 4; channel++)
        {
            if (Math.Abs(a[offset + channel] - b[offset + channel]) > ChannelTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The baseline desaturated with every differing pixel painted magenta, so
    /// the change is locatable at a glance in the CI artifact.
    /// </summary>
    private static Pixels BuildDiffImage(Pixels rendered, Pixels baseline)
    {
        var data = new byte[baseline.Data.Length];
        for (var i = 0; i < data.Length; i += 4)
        {
            if (IsDifferent(rendered.Data, baseline.Data, i))
            {
                // Bgra8888: magenta is full blue + full red, no green.
                data[i] = 0xFF;
                data[i + 1] = 0x00;
                data[i + 2] = 0xFF;
                data[i + 3] = 0xFF;
                continue;
            }

            // Luminance of the baseline, lightened, so the unchanged UI reads as
            // a faint backdrop rather than competing with the highlight.
            var luma = (byte)((baseline.Data[i] * 29 + baseline.Data[i + 1] * 150 + baseline.Data[i + 2] * 77) >> 8);
            var faded = (byte)(128 + (luma >> 1));
            data[i] = faded;
            data[i + 1] = faded;
            data[i + 2] = faded;
            data[i + 3] = 0xFF;
        }

        return new Pixels(baseline.Width, baseline.Height, data);
    }
}
