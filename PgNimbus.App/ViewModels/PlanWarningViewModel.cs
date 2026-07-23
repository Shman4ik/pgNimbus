using Avalonia.Media;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Presentation wrapper mapping a Core <see cref="PlanWarning"/> to a severity
/// glyph and accent brush. Keeps <see cref="PlanWarning"/> itself UI-free
/// (hard rule 1) while the warnings strip binds glyph/brush directly.
/// </summary>
public sealed class PlanWarningViewModel
{
    public PlanWarningViewModel(PlanWarning warning)
    {
        Warning = warning;
    }

    public PlanWarning Warning { get; }

    public string Title => Warning.Title;

    public string Detail => Warning.Detail;

    public string Glyph => Warning.Severity switch
    {
        PlanWarningSeverity.Critical => "⛔", // ⛔
        PlanWarningSeverity.Warning => "⚠",  // ⚠
        _ => "ℹ",                            // ℹ
    };

    public IBrush AccentBrush => Warning.Severity switch
    {
        PlanWarningSeverity.Critical => Danger,
        PlanWarningSeverity.Warning => Amber,
        _ => Accent,
    };

    // Fixed tokens so severity color is theme-independent, matching the AppDanger* approach.
    private static readonly IBrush Danger = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x2D, 0x7F, 0xF9));
}
