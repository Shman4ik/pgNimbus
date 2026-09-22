using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace PgNimbus.App.Views;

/// <summary>
/// The acknowledgement a copy button owes the user: a clipboard write is
/// otherwise invisible, so a click that worked and a click that missed look
/// the same. Swaps the button's content for a check mark for a moment, then
/// puts the original back.
/// </summary>
/// <remarks>
/// The button's size is pinned while the check shows, so a "Copy" label turning
/// into a narrower glyph doesn't shift its neighbours. A second click while the
/// check is up restarts the timer rather than stacking a second swap, which
/// would capture the check itself as the "original" content.
/// </remarks>
public static class CopyFeedback
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1200);

    private static readonly ConditionalWeakTable<Button, Flash> Active = new();

    public static void Show(Button button)
    {
        if (Active.TryGetValue(button, out var running))
        {
            running.Timer.Stop();
            running.Timer.Start();
            return;
        }

        var flash = new Flash(button);
        Active.Add(button, flash);
        flash.Timer.Tick += (_, _) =>
        {
            flash.Timer.Stop();
            flash.Restore();
            Active.Remove(button);
        };

        var size = button.Content is PathIcon icon ? icon.Width : 12;
        var check = new PathIcon { Width = size, Height = size };
        if (button.TryFindResource("CheckIconGeometry", button.ActualThemeVariant, out var geometry) && geometry is Geometry g)
        {
            check.Data = g;
        }
        if (button.TryFindResource("AppSuccessBrush", button.ActualThemeVariant, out var brush) && brush is IBrush b)
        {
            check.Foreground = b;
        }

        button.MinWidth = Math.Max(button.MinWidth, button.Bounds.Width);
        button.MinHeight = Math.Max(button.MinHeight, button.Bounds.Height);
        button.Content = check;
        flash.Timer.Start();
    }

    private sealed class Flash(Button button)
    {
        private readonly object? _content = button.Content;
        private readonly double _minWidth = button.MinWidth;
        private readonly double _minHeight = button.MinHeight;

        public DispatcherTimer Timer { get; } = new() { Interval = Duration };

        public void Restore()
        {
            button.Content = _content;
            button.MinWidth = _minWidth;
            button.MinHeight = _minHeight;
        }
    }
}
