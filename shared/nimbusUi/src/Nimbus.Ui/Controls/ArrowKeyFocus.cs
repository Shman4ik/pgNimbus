using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Nimbus.Ui.Controls;

/// <summary>
/// Arrow keys along a strip (a segmented <see cref="ListBox"/>, a tab strip) that keep the
/// focus ring on the item they reach (DESIGN.md rule 20).
/// <para>
/// <see cref="SelectingItemsControl"/>'s own arrow handling moves the selection and focuses
/// the next item with no navigation method, so the item is focused but not as keyboard
/// focus: <c>:focus-visible</c> stays off and the ring disappears on the first key press. A
/// list with a selection accent still shows where the keyboard is; a strip, whose selected
/// segment keeps the same wash whether or not it has focus, shows nothing. With
/// <see cref="IsEnabledProperty"/> set, this handles Left/Up, Right/Down, Home and End
/// itself, selects the item and focuses it as directional (keyboard) focus.
/// </para>
/// <para>
/// Set it from a style on the strip's class (<c>&lt;Setter Property="nui:ArrowKeyFocus.IsEnabled"
/// Value="True" /&gt;</c>), so every strip of that class gets it in one place.
/// </para>
/// </summary>
public static class ArrowKeyFocus
{
    /// <summary>Whether the arrow keys move the selection with keyboard focus.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SelectingItemsControl, bool>("IsEnabled", typeof(ArrowKeyFocus));

    static ArrowKeyFocus()
    {
        IsEnabledProperty.Changed.AddClassHandler<SelectingItemsControl>(OnIsEnabledChanged);
    }

    /// <summary>Gets <see cref="IsEnabledProperty"/>.</summary>
    public static bool GetIsEnabled(SelectingItemsControl control) => control.GetValue(IsEnabledProperty);

    /// <summary>Sets <see cref="IsEnabledProperty"/>.</summary>
    public static void SetIsEnabled(SelectingItemsControl control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(SelectingItemsControl control, AvaloniaPropertyChangedEventArgs e)
    {
        // Tunnel, so the control's own handler never sees a key this one took.
        control.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        if (e.NewValue is true)
        {
            control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || sender is not SelectingItemsControl strip
            || e.Source is not Visual source)
        {
            return;
        }

        // Only a key pressed on one of this strip's own items: a strip nested in an item's
        // content, or a text box inside one, keeps its keys.
        if (source.GetSelfAndVisualAncestors().OfType<TextBox>().Any()
            || source.GetSelfAndVisualAncestors().OfType<Control>()
                .FirstOrDefault(c => ItemsControl.ItemsControlFromItemContainer(c) == strip) is not { } from)
        {
            return;
        }

        var current = strip.IndexFromContainer(from);
        var count = strip.ItemCount;
        if (current < 0 || count == 0)
        {
            return;
        }

        int? target = e.Key switch
        {
            Key.Left or Key.Up => Step(strip, current, -1),
            Key.Right or Key.Down => Step(strip, current, +1),
            Key.Home => Step(strip, -1, +1),
            Key.End => Step(strip, count, -1),
            _ => null,
        };

        if (target is not { } index)
        {
            return;
        }

        e.Handled = true;
        if (index == current)
        {
            return;
        }

        strip.SelectedIndex = index;
        if (strip.ContainerFromIndex(index) is { } container)
        {
            container.Focus(NavigationMethod.Directional);
            container.BringIntoView();
        }
    }

    /// <summary>
    /// The next item from <paramref name="from"/> in <paramref name="direction"/> that can take
    /// focus, skipping hidden and disabled ones; <paramref name="from"/> itself at either end.
    /// </summary>
    private static int Step(SelectingItemsControl strip, int from, int direction)
    {
        for (var i = from + direction; i >= 0 && i < strip.ItemCount; i += direction)
        {
            if (strip.ContainerFromIndex(i) is not { IsVisible: true, IsEnabled: true })
            {
                continue;
            }

            return i;
        }

        return Math.Clamp(from, 0, strip.ItemCount - 1);
    }
}
