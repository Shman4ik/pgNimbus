using Avalonia;
using PgNimbus.Core.Localization;

namespace PgNimbus.App.Localization;

/// <summary>
/// The text Avalonia's Fluent theme draws itself (a text box's Cut / Copy / Paste
/// menu, the date picker's Day / Month / Year), which it reads as
/// <c>DynamicResource</c>s: written into the application's resources from
/// <see cref="Strings"/>, and again after every change of language. Application
/// resources are looked up before the theme's, so they win.
/// </summary>
internal static class FrameworkStrings
{
    private static readonly (string Resource, StringKey Key)[] Map =
    [
        ("StringTextFlyoutCutText", StringKey.Framework_Cut),
        ("StringTextFlyoutCopyText", StringKey.Framework_Copy),
        ("StringTextFlyoutPasteText", StringKey.Framework_Paste),
        ("StringDatePickerDayText", StringKey.Framework_Day),
        ("StringDatePickerMonthText", StringKey.Framework_Month),
        ("StringDatePickerYearText", StringKey.Framework_Year),
        ("StringTimePickerHourText", StringKey.Framework_Hour),
        ("StringTimePickerMinuteText", StringKey.Framework_Minute),
        ("StringTimePickerSecondText", StringKey.Framework_Second),
    ];

    public static void Install(Application app)
    {
        Apply(app);
        Localizer.LanguageChanged += () => Apply(app);
    }

    private static void Apply(Application app)
    {
        foreach (var (resource, key) in Map)
        {
            app.Resources[resource] = Strings.Get(key);
        }
    }
}
