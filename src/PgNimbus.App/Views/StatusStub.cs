using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nimbus.Ui.Converters;

namespace PgNimbus.App.Views;

/// <summary>
/// Hides a one-line status message that the bar has squeezed to a stub:
/// <c>views:StatusStub.MinWidth="100"</c>. The main window's message takes what the
/// segments beside it leave, and with the staged-changes segment up a translation
/// left "已暂存 co…" (English: "Stage…"), a fragment that says nothing and reads as
/// a fault. Below the width given, a cut message is not drawn; it is still there for
/// its tooltip (statusMessage shows the whole text on hover while it is cut), and it
/// comes back as soon as the bar has room. Opacity, not visibility, so hiding it
/// cannot change the layout that decided to hide it.
/// </summary>
public static class StatusStub
{
    public static readonly AttachedProperty<double> MinWidthProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, double>("MinWidth", typeof(StatusStub));

    static StatusStub()
    {
        MinWidthProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            if (e.OldValue is 0.0)
            {
                block.PropertyChanged += (_, change) =>
                {
                    if (change.Property == Visual.BoundsProperty)
                    {
                        Update(block);
                    }
                    else if (change.Property == TextBlock.TextProperty)
                    {
                        // A new text whose box keeps its width: judge it once it is laid out.
                        Dispatcher.UIThread.Post(() => Update(block), DispatcherPriority.Loaded);
                    }
                };
            }

            Update(block);
        });
    }

    public static double GetMinWidth(TextBlock block) => block.GetValue(MinWidthProperty);

    public static void SetMinWidth(TextBlock block, double value) => block.SetValue(MinWidthProperty, value);

    private static void Update(TextBlock block)
    {
        var stub = block.Bounds.Width > 0
                   && block.Bounds.Width < GetMinWidth(block)
                   && CutTextTip.IsCut(block);
        if (stub)
        {
            block.Opacity = 0;
        }
        else
        {
            block.ClearValue(Visual.OpacityProperty);
        }
    }
}
