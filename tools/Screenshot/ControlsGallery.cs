using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace PgNimbus.Screenshot;

/// <summary>
/// The shared control states of DESIGN.md rule 20 side by side, which no app screen
/// shows at once: a tree and a list with their selection in the focused (accent) and
/// unfocused (grey) faces, the keyboard focus ring on a button, switches on and off,
/// and a menu with its highlighted item. A focus ring and a focused list only exist
/// while something holds keyboard focus, which no other scenario arranges.
/// </summary>
public static class ControlsGallery
{
    public static Window Build()
    {
        var focusedTree = new TreeView
        {
            Height = 120,
            ItemsSource = new[] { "public", "analytics", "billing" },
        };
        focusedTree.SelectedItem = "analytics";

        var unfocusedList = new ListBox
        {
            Height = 120,
            ItemsSource = new[] { "Daily report", "Slow orders", "Top customers" },
            SelectedIndex = 1,
        };

        var ringButton = new Button { Content = "Focus ring", Classes = { "soft" } };
        var ringChip = new Button { Content = "?", Classes = { "chip" }, Padding = new Thickness(5) };

        var menu = new MenuFlyoutPresenter
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = new object[]
            {
                new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Meta) },
                new MenuItem { Header = "Copy as" },
                new Separator(),
                new MenuItem { Header = "Inspect cell…" },
                new MenuItem { Header = "Set cell to NULL", IsEnabled = false },
            },
        };

        var window = new Window
        {
            Width = 720,
            Height = 420,
            Content = new Grid
            {
                Margin = new Thickness(20),
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                Children =
                {
                    Column(0,
                        Caption("Tree, focused"), focusedTree,
                        Caption("List, not focused"), unfocusedList),
                    Column(1,
                        Caption("Focus ring"), Row(ringButton, ringChip),
                        Caption("Switches"),
                        Row(new ToggleSwitch { IsChecked = true, OnContent = "", OffContent = "" },
                            new ToggleSwitch { IsChecked = false, OnContent = "", OffContent = "" },
                            new ToggleSwitch { IsChecked = true, IsEnabled = false, OnContent = "", OffContent = "" })),
                    Column(2, Caption("Menu"), menu),
                },
            },
        };

        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // The highlighted menu item is the pointer's or the keyboard's; mark one.
            if (menu.ItemsSource is object[] items && items[3] is MenuItem inspect)
            {
                ((IPseudoClasses)inspect.Classes).Add(":selected");
            }

            // One control holds focus at a time, and this frame needs two looks that
            // each depend on it: the button takes real keyboard focus (so the ring is
            // the adorner layer's own), and the tree is given the pseudo-class a
            // focused descendant would set, which is all its accent selection reads.
            ringButton.Focus(NavigationMethod.Tab);
            ((IPseudoClasses)focusedTree.Classes).Add(":focus-within");
        });

        return window;
    }

    private static TextBlock Caption(string text) =>
        new() { Text = text, Classes = { "sectionHeader" }, Margin = new Thickness(0, 12, 0, 6) };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.AddRange(children);
        return row;
    }

    private static StackPanel Column(int column, params Control[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        panel.Children.AddRange(children);
        Grid.SetColumn(panel, column);
        return panel;
    }
}
