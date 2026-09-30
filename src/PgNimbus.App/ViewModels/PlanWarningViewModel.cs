using Avalonia.Media;
using PgNimbus.Core.Query;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Presentation wrapper mapping a Core <see cref="PlanWarning"/> to a severity
/// glyph and accent brush. Keeps <see cref="PlanWarning"/> itself UI-free
/// (hard rule 1) while the warnings strip binds glyph/brush directly.
/// </summary>
public sealed class PlanWarningViewModel(PlanWarning warning)
{
    public PlanWarning Warning { get; } = warning;

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

    /// <summary>Low-alpha row background matching the severity, so an info note isn't shown on a danger wash.</summary>
    public IBrush WashBrush => Warning.Severity switch
    {
        PlanWarningSeverity.Critical => DangerWash,
        PlanWarningSeverity.Warning => AmberWash,
        _ => AccentWash,
    };

    // Fixed tokens so severity color is theme-independent, matching the AppDanger* approach.
    private static readonly IBrush Danger = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x2D, 0x7F, 0xF9));
    private static readonly IBrush DangerWash = new SolidColorBrush(Color.FromArgb(0x22, 0xE5, 0x48, 0x4D));
    private static readonly IBrush AmberWash = new SolidColorBrush(Color.FromArgb(0x22, 0xE0, 0x8A, 0x1E));
    private static readonly IBrush AccentWash = new SolidColorBrush(Color.FromArgb(0x1E, 0x2D, 0x7F, 0xF9));
}
