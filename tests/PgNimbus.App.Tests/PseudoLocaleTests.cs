using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using PgNimbus.Core.Localization;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// Proves no interface text is left out of <c>Strings.resx</c>: every screenshot
/// scenario is built in the pseudo-locale, where every string from the table reads
/// <c>[Àççéñţéð]</c>, and any visible text with letters that carries no such
/// bracket was written somewhere else: in XAML, a view model or code-behind. Data
/// the fixtures show (names, values, SQL) is told apart by where it is drawn
/// (<see cref="IsData"/>) or listed in <see cref="DataText"/>.
/// <para>
/// <see cref="Converted"/> is the scenarios already moved to the table; the list
/// grows with each slice until it is <c>Scenarios.All</c>.
/// </para>
/// </summary>
[NotInParallel]
public partial class PseudoLocaleTests
{
    /// <summary>
    /// The scenarios whose every string is in the table, with the part of the window
    /// that is checked: an overlay scenario shows the main window behind it, which
    /// has its own entry. Null checks the whole window.
    /// </summary>
    private static readonly Dictionary<string, Type?> Converted = new()
    {
        ["about-window"] = typeof(Views.AboutView),
        ["preferences-window"] = typeof(Views.PreferencesView),
        ["preferences-window-appearance"] = typeof(Views.PreferencesView),
        ["preferences-window-data"] = typeof(Views.PreferencesView),
    };

    /// <summary>Text that is data in the fixtures, or a name never translated.</summary>
    private static readonly HashSet<string> DataText =
    [
        "pgNimbus",
    ];

    [GeneratedRegex(@"\p{L}{2,}")]
    private static partial Regex Word();

    [Test]
    public async Task Every_visible_string_in_a_converted_window_comes_from_the_table()
    {
        try
        {
            await Ui.Run(async () =>
            {
                Localizer.Load(Localizer.Pseudo);
                var leftovers = new List<string>();
                var scanned = 0;
                foreach (var (name, build) in Scenarios.All.Where(s => Converted.ContainsKey(s.Name)))
                {
                    var window = build();
                    Ui.Show(window);
                    Ui.Settle();

                    Visual root = Converted[name] is { } scope
                        ? window.GetVisualDescendants().First(v => v.GetType() == scope)
                        : window;

                    // Every tab of every tab strip, not only the one the scenario opened on.
                    var pages = root.GetVisualDescendants().OfType<TabControl>().ToList();
                    var states = new List<Action> { () => { } };
                    foreach (var tabs in pages)
                    {
                        for (var i = 0; i < tabs.ItemCount; i++)
                        {
                            var index = i;
                            states.Add(() => tabs.SelectedIndex = index);
                        }
                    }

                    foreach (var state in states)
                    {
                        state();
                        Ui.Settle();
                        foreach (var (where, text) in Texts(root))
                        {
                            scanned++;
                            var line = $"{name}: {where}: \"{text}\"";
                            if (!IsTranslated(text) && !leftovers.Contains(line))
                            {
                                leftovers.Add(line);
                            }
                        }
                    }

                    window.Close();
                    Ui.Settle();
                }

                await Assert.That(scanned).IsGreaterThan(Converted.Count);
                await Assert.That(leftovers).IsEmpty().Because(string.Join(Environment.NewLine, leftovers));
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
    }

    private static bool IsTranslated(string text) =>
        !Word().IsMatch(text) || text.Contains('[') || DataText.Contains(text.Trim());

    /// <summary>Every piece of text a window shows, with the element it is on.</summary>
    internal static IEnumerable<(string Where, string Text)> Texts(Visual root)
    {
        if (root is Window { Title: { Length: > 0 } title })
        {
            yield return ("Window.Title", title);
        }

        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is not Control control || !control.IsEffectivelyVisible || IsData(control))
            {
                continue;
            }

            if (control is TextBlock { Text: { Length: > 0 } text })
            {
                yield return (Describe(control), text);
            }

            if (ToolTip.GetTip(control) is string tip)
            {
                yield return (Describe(control) + " tooltip", tip);
            }

            if (control is TextBox { PlaceholderText: { Length: > 0 } placeholder })
            {
                yield return (Describe(control) + " placeholder", placeholder);
            }
        }
    }

    /// <summary>
    /// Text drawn where only data goes: a grid cell or column header, an editor, the
    /// text a person typed into a box.
    /// </summary>
    private static bool IsData(Control control) =>
        control.GetVisualAncestors().Any(a => a is DataGridCell or DataGridColumnHeader or AvaloniaEdit.TextEditor or TextPresenter)
        || control is TextPresenter;

    private static string Describe(Control control)
    {
        var named = control.GetVisualAncestors().OfType<Control>().Prepend(control).FirstOrDefault(c => !string.IsNullOrEmpty(c.Name));
        return control.GetType().Name + (named is null ? "" : " in " + named.Name);
    }
}
