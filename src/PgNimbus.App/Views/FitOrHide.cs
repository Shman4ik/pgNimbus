using Avalonia;
using Avalonia.Controls;

namespace PgNimbus.App.Views;

/// <summary>
/// Shows its child at its full width or not at all. The connection dialog's
/// identity button (mark, name, version) sits between New and the form's buttons
/// and gets what they leave; a translation with longer button text ("Se
/// connecter", "Подключиться") left it too narrow, and the name and version were
/// drawn past its edge. It used to step aside below a fixed window width, which no
/// translation could be measured against. The width the child needs is learned
/// while it is shown and kept while it is hidden, so hiding it cannot change the
/// answer.
/// </summary>
public class FitOrHide : Decorator
{
    private double _needed;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        if (child.IsVisible)
        {
            child.Measure(Size.Infinity);
            _needed = child.DesiredSize.Width;
        }

        var fits = availableSize.Width >= _needed;
        if (child.IsVisible != fits)
        {
            child.IsVisible = fits;
            if (fits)
            {
                child.Measure(Size.Infinity);
                _needed = child.DesiredSize.Width;
            }
        }

        return child.IsVisible
            ? new Size(Math.Min(_needed, availableSize.Width), child.DesiredSize.Height)
            : default;
    }
}
