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

    private static bool _subscribed;

    public static void Install(Application app)
    {
        Apply(app);

        // One handler for the process, writing to the application of the moment.
        // It used to be one per Install capturing its app, and a static event
        // holds its handlers strongly: the UI tests get a new Application per
        // test, so a full run kept ~570 of them alive with their windows, and every
        // change of language rewrote these nine resources in each, walking every
        // window's tree nine times. The pseudo-locale scan, which switches twice per
        // scenario, took 29 minutes on Linux and CI timed out. Holding the apps
        // weakly is not enough: an old test's Application stays reachable through
        // geometries and brushes kept in statics, which hold their dispatcher.
        if (!_subscribed)
        {
            _subscribed = true;
            Localizer.LanguageChanged += ApplyToCurrent;
        }
    }

    private static void ApplyToCurrent()
    {
        if (Application.Current is { } app)
        {
            Apply(app);
        }
    }

    private static void Apply(Application app)
    {
        foreach (var (resource, key) in Map)
        {
            app.Resources[resource] = Strings.Get(key);
        }
    }
}
