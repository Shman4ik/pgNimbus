using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Nimbus.Ui.Converters;
using PgNimbus.Core.Localization;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Finds text that fits in English and no longer fits in a translation. Every
/// scenario the pseudo-locale scan covers (<see cref="PseudoLocaleTests.Converted"/>)
/// is built in English and then in each language, every tab of every tab strip
/// is opened, and each text block is measured: cut short by trimming or a line
/// limit, wider than the box it was given, taller than its box, or drawn past a
/// clipping ancestor or the window's edge. The two builds have the same tree, so
/// an element is matched with its English twin by its position in the visual tree,
/// and only what fits in English is reported: a text that is cut in English too is
/// a layout matter, not the translation's.
/// <para>
/// The languages are <c>qps-ploc</c> and every <c>Strings.&lt;tag&gt;.resx</c> in
/// <c>src/PgNimbus.Core/Localization</c>, read from the file, so a translation that
/// is not built in yet is checked too. <b>Report-only</b>: findings are written to
/// the test output and to <see cref="ReportPath"/>, and the test fails only when it
/// compared nothing. Run it alone:
/// <code>
/// dotnet run --project tests/PgNimbus.App.Tests -c Release -- --treenode-filter "/*/*/TranslationFitTests/*"
/// </code>
/// <c>PGNIMBUS_FIT_LANGUAGES=de,ru</c> narrows the languages,
/// <c>PGNIMBUS_FIT_SCENARIOS=connection</c> the scenarios (a substring of the name),
/// and <c>PGNIMBUS_FIT_REPORT</c> names the report file.
/// </para>
/// </summary>
[NotInParallel]
[Explicit]
public class TranslationFitTests
{
    /// <summary>A pixel of slack: layout rounds, and a half-pixel overhang is not a cut.</summary>
    private const double Slack = 1.0;

    private static string ReportPath =>
        Environment.GetEnvironmentVariable("PGNIMBUS_FIT_REPORT") is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : Path.Combine(AppContext.BaseDirectory, "TestResults", "translation-fit.md");

    [Test]
    public async Task Report_text_that_fits_in_English_but_not_in_a_translation()
    {
        var languages = Languages();
        var scenarioFilter = Environment.GetEnvironmentVariable("PGNIMBUS_FIT_SCENARIOS");
        var scenarios = Scenarios.All
            .Where(s => PseudoLocaleTests.Converted.ContainsKey(s.Name))
            .Where(s => string.IsNullOrEmpty(scenarioFilter) || s.Name.Contains(scenarioFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var findings = new List<Finding>();
        var compared = 0;
        var unmatched = 0;
        var cutInEnglish = 0;

        try
        {
            await Ui.Run(() =>
            {
                foreach (var (name, build) in scenarios)
                {
                    Localizer.Load(Localizer.English);
                    var english = Measure(name, build);
                    cutInEnglish += english.Values.Count(m => m.Problem is not null);

                    foreach (var language in languages)
                    {
                        language.Load();
                        foreach (var (key, measured) in Measure(name, build))
                        {
                            if (!english.TryGetValue(key, out var twin))
                            {
                                unmatched++;
                                continue;
                            }

                            compared++;
                            // Text shown in every tab state is measured once per state;
                            // it is one finding.
                            if (measured.Problem is { } problem && twin.Problem is null)
                            {
                                var finding = new Finding(language.Tag, name, measured.Where, twin.Text, measured.Text, problem);
                                if (!findings.Contains(finding))
                                {
                                    findings.Add(finding);
                                }
                            }
                        }
                    }
                }

                return Task.CompletedTask;
            });
        }
        finally
        {
            await Ui.Run(() =>
            {
                Localizer.Load(Localizer.English);
                return Task.CompletedTask;
            });
        }

        var report = Report(languages, scenarios.Count, compared, unmatched, cutInEnglish, findings);
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        await File.WriteAllTextAsync(ReportPath, report);
        Console.WriteLine(report);
        Console.WriteLine($"Report written to {ReportPath}");

        // Report-only: the owner decides per language what has to fit. A run that
        // compared nothing, though, proves nothing, and that is a failure.
        await Assert.That(scenarios).IsNotEmpty();
        await Assert.That(compared).IsGreaterThan(0);
    }

    /// <summary>A language to check: the pseudo-locale, or a translation file.</summary>
    private sealed record Language(string Tag, Action Load);

    private static List<Language> Languages()
    {
        var all = new List<Language> { new(Localizer.Pseudo, () => Localizer.Load(Localizer.Pseudo)) };
        var folder = Path.Combine(RepoRoot(), "src", "PgNimbus.Core", "Localization");
        foreach (var file in Directory.GetFiles(folder, "Strings.*.resx").Order(StringComparer.Ordinal))
        {
            var tag = Path.GetFileNameWithoutExtension(file)["Strings.".Length..];
            var table = ReadTable(file);
            all.Add(new Language(tag, () => Localizer.LoadTable(tag, table)));
        }

        var only = Environment.GetEnvironmentVariable("PGNIMBUS_FIT_LANGUAGES");
        if (string.IsNullOrWhiteSpace(only))
        {
            return all;
        }

        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. all.Where(l => wanted.Contains(l.Tag, StringComparer.OrdinalIgnoreCase))];
    }

    private static Dictionary<string, string> ReadTable(string file)
    {
        using var stream = File.OpenRead(file);
        return Localizer.Read(stream);
    }

    /// <summary>What one text block measured, and why it doesn't fit, if it doesn't.</summary>
    private sealed record Measured(string Where, string Text, string? Problem);

    private sealed record Finding(string Language, string Scenario, string Where, string English, string Text, string Problem);

    /// <summary>
    /// Builds the scenario in the language loaded now and measures every text block in
    /// every tab state, keyed by the state and the block's position in the visual tree.
    /// </summary>
    private static Dictionary<string, Measured> Measure(string scenario, Func<Window> build)
    {
        var result = new Dictionary<string, Measured>(StringComparer.Ordinal);
        var window = build();
        Ui.Show(window);
        Ui.Settle();
        try
        {
            Visual root = PseudoLocaleTests.Converted[scenario] is { } scope
                ? window.GetVisualDescendants().First(v => v.GetType() == scope)
                : window;

            // Every tab of every tab strip, as the pseudo-locale scan opens them.
            var states = new List<(string Key, Action Apply)> { ("", () => { }) };
            var strips = root.GetVisualDescendants().OfType<TabControl>().ToList();
            for (var s = 0; s < strips.Count; s++)
            {
                for (var i = 0; i < strips[s].ItemCount; i++)
                {
                    var (strip, index) = (strips[s], i);
                    states.Add(($"tabs{s}={i} ", () => strip.SelectedIndex = index));
                }
            }

            foreach (var (state, apply) in states)
            {
                apply();
                Ui.Settle();
                foreach (var (path, block) in Blocks(root, ""))
                {
                    if (Check(window, block) is { } measured)
                    {
                        result.TryAdd(state + path, measured);
                    }
                }
            }
        }
        finally
        {
            window.Close();
            Ui.Settle();
        }

        return result;
    }

    /// <summary>Every visible text block under <paramref name="visual"/>, with its tree path.</summary>
    private static IEnumerable<(string Path, TextBlock Block)> Blocks(Visual visual, string path)
    {
        var index = 0;
        foreach (var child in visual.GetVisualChildren())
        {
            var here = $"{path}/{child.GetType().Name}[{index++}]";
            if (child is Control { IsEffectivelyVisible: false })
            {
                continue;
            }

            if (child is TextBlock { Text.Length: > 0 } block && !PseudoLocaleTests.IsData(block))
            {
                yield return (here, block);
            }

            foreach (var inner in Blocks(child, here))
            {
                yield return inner;
            }
        }
    }

    private static Measured? Check(Window window, TextBlock block)
    {
        var text = block.Text!;
        var padding = block.Padding;
        var width = block.Bounds.Width - padding.Left - padding.Right;
        var height = block.Bounds.Height - padding.Top - padding.Bottom;
        if (width <= 0 || height <= 0 || block.Opacity == 0)
        {
            return null;
        }

        var where = Describe(block);
        var oneLine = block.TextWrapping == TextWrapping.NoWrap;
        var natural = NaturalWidth(block);

        string? problem = null;
        if (CutTextTip.IsCut(block))
        {
            problem = oneLine
                ? $"cut short: needs {natural:0} px, has {width:0} px"
                : $"cut short after {block.TextLayout.TextLines.Count} line(s) ({width:0} px wide)";
        }
        else if (oneLine && natural > width + Slack)
        {
            problem = $"wider than its box: needs {natural:0} px, has {width:0} px";
        }
        else if (!oneLine && block.TextLayout.Height > height + Slack)
        {
            problem = $"taller than its box: needs {block.TextLayout.Height:0} px high, has {height:0} px";
        }
        else if (Clip(window, block, oneLine ? natural : block.TextLayout.WidthIncludingTrailingWhitespace, width) is { } clipped)
        {
            problem = clipped;
        }

        return new Measured(where, text, problem);
    }

    /// <summary>The width the text takes on one line, with nothing to stop it.</summary>
    private static double NaturalWidth(TextBlock block)
    {
        using var layout = new TextLayout(
            block.Text,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            foreground: null,
            textWrapping: TextWrapping.NoWrap,
            letterSpacing: block.LetterSpacing,
            fontFeatures: block.FontFeatures);
        return layout.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// Whether the drawn text crosses a clipping ancestor or the window's edge
    /// sideways. Only the horizontal extent counts: a row half scrolled out of a list
    /// is scrolling, not a cut.
    /// </summary>
    private static string? Clip(Window window, TextBlock block, double drawn, double width)
    {
        if (block.TranslatePoint(new Point(block.Padding.Left, block.Padding.Top), window) is not { } origin)
        {
            return null;
        }

        var shift = block.TextAlignment switch
        {
            TextAlignment.Center => (width - drawn) / 2,
            TextAlignment.Right or TextAlignment.End => width - drawn,
            _ => 0,
        };
        var text = new Rect(origin.X + Math.Min(0, shift), origin.Y, Math.Max(drawn, width), block.TextLayout.Height);

        var clips = new List<(Rect Rect, string What)> { (new Rect(window.ClientSize), "the window's edge") };
        foreach (var ancestor in block.GetVisualAncestors().OfType<Visual>())
        {
            if (ancestor is Window || !ancestor.ClipToBounds || ancestor.TranslatePoint(default, window) is not { } at)
            {
                continue;
            }

            clips.Add((new Rect(at, ancestor.Bounds.Size), "its " + ancestor.GetType().Name));
        }

        foreach (var (clip, what) in clips)
        {
            // Not on screen at all (scrolled away vertically): nothing to judge.
            if (text.Bottom <= clip.Top || text.Top >= clip.Bottom)
            {
                return null;
            }

            var visible = Math.Min(text.Right, clip.Right) - Math.Max(text.Left, clip.Left);
            if (visible < 0)
            {
                return null;
            }

            if (visible + Slack < Math.Min(drawn, text.Width))
            {
                return $"runs past {what}: needs {drawn:0} px, {Math.Max(0, visible):0} px visible";
            }
        }

        return null;
    }

    /// <summary>
    /// "TextBlock in Button (ConnectButton)": the block, the control that owns it if it
    /// is a button, tab, menu item, list row or header, and the nearest name.
    /// </summary>
    private static string Describe(TextBlock block)
    {
        var ancestors = block.GetVisualAncestors().OfType<Control>().ToList();
        var owner = ancestors.FirstOrDefault(a => a is Button or ToggleButton or TabItem or MenuItem or HeaderedContentControl
            or ListBoxItem or ComboBox or CheckBox or RadioButton or DataGridColumnHeader);
        // A template part's name (PART_ContentPresenter) says little; a name the view
        // gave says where the text is.
        var named = ancestors.Prepend(block).FirstOrDefault(c => c.Name is { Length: > 0 } n && !n.StartsWith("PART_", StringComparison.Ordinal))
            ?? ancestors.Prepend(block).FirstOrDefault(c => !string.IsNullOrEmpty(c.Name));
        var text = new StringBuilder(block.GetType().Name);
        if (owner is not null)
        {
            text.Append(" in ").Append(owner.GetType().Name);
        }

        if (named is not null)
        {
            text.Append(" (").Append(named.Name).Append(')');
        }

        return text.ToString();
    }

    private static string Report(
        IReadOnlyList<Language> languages, int scenarios, int compared, int unmatched, int cutInEnglish, List<Finding> findings)
    {
        var text = new StringBuilder();
        text.AppendLine("## Text that fits in English but not in a translation");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{scenarios} scenarios, {languages.Count} language(s), {compared} text blocks compared with their English twin; " +
            $"{unmatched} without a twin (the tree differed), {cutInEnglish} already cut in English (not counted).");
        text.AppendLine();
        text.AppendLine("| Language | Findings |");
        text.AppendLine("| --- | ---: |");
        foreach (var language in languages)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| `{language.Tag}` | {findings.Count(f => f.Language == language.Tag)} |");
        }

        foreach (var group in findings.GroupBy(f => f.Language))
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"### `{group.Key}`");
            text.AppendLine();
            text.AppendLine("| Scenario | Element | English | Translation | Problem |");
            text.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var f in group.OrderBy(f => f.Scenario, StringComparer.Ordinal))
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {f.Scenario} | {Cell(f.Where)} | {Cell(f.English)} | {Cell(f.Text)} | {f.Problem} |");
            }
        }

        return text.ToString();
    }

    private static string Cell(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);
        return flat.Length > 80 ? flat[..79] + "…" : flat;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PgNimbus.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
