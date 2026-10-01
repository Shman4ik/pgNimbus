using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// A tooltip opens only on the element the pointer is over, and Avalonia's hit
/// test finds an element only where it draws something. A TextBlock or a panel
/// with no background draws nothing of its own (glyphs are not hit-testable), so
/// the pointer over its text reaches whatever is behind it — a list row, a card,
/// the window — and the tooltip never opens. The 1.0.1 release pass found it on
/// the main status line (#332: 0 of 4,536 points over the text reached the
/// TextBlock); this walks every window the harness renders for the same defect.
/// </summary>
public class TooltipReachTests
{
    [Test]
    public async Task The_pointer_over_every_tooltip_reaches_the_element_that_carries_it()
    {
        await Ui.Run(async () =>
        {
            var dead = new List<string>();
            var probed = 0;
            foreach (var (name, build) in Scenarios.All)
            {
                var window = build();
                Ui.Show(window);
                Ui.Settle();

                foreach (var (element, point) in Probes(window))
                {
                    // Covered by something unrelated (an overlay, a popup's
                    // scrim): not this defect, and not testable from here.
                    var hit = window.InputHitTest(point) as Visual;
                    if (hit is not null && !ReferenceEquals(hit, element) && !element.GetVisualAncestors().Contains(hit)
                        && !hit.GetVisualAncestors().Contains(element))
                    {
                        continue;
                    }

                    probed++;
                    if (hit is null || !(ReferenceEquals(hit, element) || hit.GetVisualAncestors().Contains(element)))
                    {
                        dead.Add($"{name}: {Describe(element)} (pointer reached {hit?.GetType().Name ?? "nothing"})");
                    }
                }

                window.Close();
                Ui.Settle();
            }

            // Not vacuous: the walk has to find the tooltips it is checking.
            await Assert.That(probed).IsGreaterThan(40);
            await Assert.That(dead).IsEmpty().Because(string.Join(Environment.NewLine, dead));
        });
    }

    /// <summary>
    /// The hit test above is what decides it, but the claim is that a tooltip
    /// opens: a history row's run detail (when, where) is the case a person hovers
    /// for, and it sits inside a list item whose own background used to take the
    /// pointer.
    /// </summary>
    [Test]
    public async Task A_history_row_shows_its_run_detail_on_hover()
    {
        await Ui.Run(async () =>
        {
            var window = Scenarios.QueriesSidebar();
            Ui.Show(window);
            Ui.Settle();

            var detail = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.IsEffectivelyVisible && ToolTip.GetTip(t) is string tip && tip.StartsWith("Ran ", StringComparison.Ordinal));
            await Assert.That(await StatusLineTests.HoverAsync(window, detail)).IsEqualTo(ToolTip.GetTip(detail));

            window.Close();
        });
    }

    /// <summary>
    /// The fill is a current value under everything else: a background the markup or
    /// a style sets is drawn as written, before and after the tooltip arrives, and one
    /// that goes back to null (a style that stops matching) is filled again rather
    /// than leaving the tooltip dead from then on.
    /// </summary>
    [Test]
    public async Task A_tooltip_never_changes_a_background_something_else_set()
    {
        await Ui.Run(async () =>
        {
            var styled = new TextBlock { Text = "styled" };
            ToolTip.SetTip(styled, "tip");
            var local = new StackPanel { Background = Brushes.Red };
            ToolTip.SetTip(local, "tip");
            var window = new Window
            {
                Width = 300,
                Height = 200,
                Content = new StackPanel { Children = { styled, local } },
            };
            window.Styles.Add(new Style(x => x.OfType<TextBlock>().Class("tinted"))
            {
                Setters = { new Setter(TextBlock.BackgroundProperty, Brushes.Green) },
            });
            Ui.Show(window);

            await Assert.That(styled.Background).IsEqualTo(Brushes.Transparent);
            await Assert.That(local.Background).IsEqualTo(Brushes.Red);

            styled.Classes.Add("tinted");
            Ui.Settle();
            await Assert.That(styled.Background).IsEqualTo(Brushes.Green);

            styled.Classes.Remove("tinted");
            Ui.Settle();
            await Assert.That(styled.Background).IsEqualTo(Brushes.Transparent);

            window.Close();
        });
    }

    /// <summary>
    /// Every visible element carrying a tooltip, with a point over what it shows:
    /// the middle of its text for a text block or a panel holding one, the middle
    /// of the element otherwise.
    /// </summary>
    private static IEnumerable<(Control Element, Point Point)> Probes(Window window)
    {
        foreach (var element in window.GetVisualDescendants().OfType<Control>().ToList())
        {
            // A disabled control shows no tooltip unless it opts in
            // (ToolTip.ShowOnDisabled), and the hit test skips it on purpose.
            if (ToolTip.GetTip(element) is null || !element.IsEffectivelyVisible || !element.IsEffectivelyEnabled
                || element.Bounds.Width < 1 || element.Bounds.Height < 1)
            {
                continue;
            }

            // A shape is hit where it is filled, which is what it shows.
            if (element is Shape)
            {
                continue;
            }

            var target = element as Visual;
            if (element is Panel)
            {
                target = element.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.IsEffectivelyVisible && t.Bounds.Width >= 1) ?? (Visual)element;
            }

            var middle = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
            if (target.TranslatePoint(middle, window) is not { } point || !IsOnScreen(element, point, window))
            {
                continue;
            }

            yield return (element, point);
        }
    }

    /// <summary>
    /// Whether the point is where the element can be seen and reached: inside the window,
    /// inside every ancestor that clips (a scroll viewport, a grid scrolled sideways, a
    /// narrow pane), and under nothing that has switched hit-testing off on purpose.
    /// </summary>
    private static bool IsOnScreen(Control element, Point point, Window window)
    {
        if (!new Rect(window.ClientSize).Contains(point))
        {
            return false;
        }

        foreach (var visual in element.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, window)))
        {
            if (visual is InputElement { IsHitTestVisible: false })
            {
                return false;
            }

            if (visual.ClipToBounds && visual.TranslatePoint(default, window) is { } origin
                && !new Rect(origin, visual.Bounds.Size).Contains(point))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(Control element)
    {
        var text = element switch
        {
            TextBlock t => t.Text,
            _ => element.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text,
        };
        return $"{element.GetType().Name}{(element.Name is { } n ? "#" + n : "")} \"{text}\" tip \"{ToolTip.GetTip(element)}\"";
    }
}
